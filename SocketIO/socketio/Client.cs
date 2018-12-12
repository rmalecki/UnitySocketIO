using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
//using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System.Threading;
using SocketIOClient.Eventing;
using SocketIOClient.Messages;
using WebSocketNew;

namespace SocketIOClient { 


    public sealed class WebsocketSharpFactory : IWebsocketFactory
    {

        public IWebsocket Build(WebsocketConfiguration config)
        {
            var socket = WebSocketExecutor.GetInstance();
            socket.config = config;
            return socket;
        }

        /*public IDelayedExecutor GetDelayedExecutor()
        {
            return DelayedExecutorComponent.GetInstance();
        }*/
    }

	/// <summary>
	/// Class to emulate socket.io javascript client capabilities for .net classes
	/// </summary>
	/// <exception cref = "ArgumentException">Connection for wss or https urls</exception>  
	public class Client : IDisposable, SocketIOClient.IClient
	{
        // FIXME
        //private Timer socketHeartBeatTimer; // HeartBeat timer 
        private uint socketHeartBeatTimer;
		//private Task dequeuOutBoundMsgTask;
        // FIXME
		//private Thread dequeuOutBoundMsgTask;
		private Queue<string> outboundQueue;
		private int retryConnectionCount = 0;
		private int retryConnectionAttempts = 3;
		private readonly static object padLock = new object(); // allow one connection attempt at a time
        private UnityEngine.MonoBehaviour behaviour;

        /// <summary>
        /// Uri of Websocket server
        /// </summary>
        protected Uri uri;
		/// <summary>
		/// Underlying WebSocket implementation
		/// </summary>
		protected IWebsocket wsClient;
		/// <summary>
		/// RegistrationManager for dynamic events
		/// </summary>
		protected RegistrationManager registrationManager;  // allow registration of dynamic events (event names) for client actions
		

		// Events
		/// <summary>
		/// Opened event comes from the underlying websocket client connection being opened.  This is not the same as socket.io returning the 'connect' event
		/// </summary>
		public event EventHandler Opened;
		public event EventHandler<MessageEventArgs> Message;
		public event EventHandler ConnectionRetryAttempt;
		public event EventHandler HeartBeatTimerEvent;
		/// <summary>
		/// <para>The underlying websocket connection has closed (unexpectedly)</para>
		/// <para>The Socket.IO service may have closed the connection due to a heartbeat timeout, or the connection was just broken</para>
		/// <para>Call the client.Connect() method to re-establish the connection</para>
		/// </summary>
		public event EventHandler SocketConnectionClosed;
		public event EventHandler<ErrorEventArgs> Error;

		/// <summary>
		/// ResetEvent for Outbound MessageQueue Empty Event - all pending messages have been sent
		/// </summary>
		public ManualResetEvent MessageQueueEmptyEvent = new ManualResetEvent(true);

		/// <summary>
		/// Connection Open Event
		/// </summary>
		public ManualResetEvent ConnectionOpenEvent = new ManualResetEvent(false);


		/// <summary>
		/// Number of reconnection attempts before raising SocketConnectionClosed event - (default = 3)
		/// </summary>
		public int RetryConnectionAttempts
		{
			get { return this.retryConnectionAttempts; }
			set { this.retryConnectionAttempts = value; }
		}

		/// <summary>
		/// Value of the last error message text  
		/// </summary>
		public string LastErrorMessage = "";
        private WebsocketSharpFactory socketFactory;

        /// <summary>
        /// Represents the initial handshake parameters received from the socket.io service (SID, HeartbeatTimeout etc)
        /// </summary>
        public SocketIOHandshake HandShake { get; private set; }

		/// <summary>
		/// Returns boolean of ReadyState == WebSocketState.Open
		/// </summary>
		public bool IsConnected
		{
			get
			{
				return this.ReadyState == WebSocketState.Open;
			}
		}

		/// <summary>
		/// Connection state of websocket client: None, Connecting, Open, Closing, Closed
		/// </summary>
		public WebSocketState ReadyState
		{
			get
			{
				if (this.wsClient != null)
					return this.wsClient.State;
				else
					return WebSocketState.None;
			}
		}

		// Constructors
		public Client(UnityEngine.MonoBehaviour behaviour, string url)
        {
            this.behaviour = behaviour;
            this.uri = new Uri(url);
                       

			this.registrationManager = new RegistrationManager();
			this.outboundQueue =  (new Queue<string>());
            // FIXME
            behaviour.StartCoroutine(DequeuOutboundMessages());
			//this.dequeuOutBoundMsgTask = new Thread(new ThreadStart(dequeuOutboundMessages));
			//this.dequeuOutBoundMsgTask = Task.Factory.StartNew(() => dequeuOutboundMessages(), TaskCreationOptions.LongRunning);
			//this.dequeuOutBoundMsgTask.Start();
		}

		/// <summary>
		/// Initiate the connection with Socket.IO service
		/// </summary>
		public void Connect()
		{
			lock (padLock)
			{
				if (!(this.ReadyState == WebSocketState.Connecting || this.ReadyState == WebSocketState.Open))
				{
					try
					{
						this.ConnectionOpenEvent.Reset();
                        behaviour.StartCoroutine(this.requestHandshake(uri, handshake => {
							this.HandShake = handshake;	
							if (this.HandShake == null || string.IsNullOrEmpty(this.HandShake.SID) || this.HandShake.HadError)
							{
								this.LastErrorMessage = string.Format("Error initializing handshake with {0}", uri.ToString());
								this.OnErrorEvent(this, new ErrorEventArgs(this.LastErrorMessage, new Exception()));
							}
							else
							{
								string wsScheme = (uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws");
                                var _uri = new Uri(
                                    string.Format("{0}://{1}:{2}/socket.io/1/websocket/{3}", wsScheme, uri.Host, uri.Port, this.HandShake.SID));

                                if (socketFactory == null)
                                {
                                    socketFactory = new WebsocketSharpFactory();
                                }
                                var config = new WebsocketConfiguration()
                                {
                                    uri = _uri,
                                    onOpenCallback = this.wsClient_OpenEvent,
                                    onCloseCallback = wsClient_Closed,
                                    onErrorCallback = this.wsClient_Error,
                                    onMessageCallback = this.wsClient_MessageReceived
                                };
                                this.wsClient = socketFactory.Build(config);
                                
                                wsClient.Connect();                                	
							}
						}));
					}
					catch (Exception ex)
					{
						Trace.WriteLine(string.Format("Connect threw an exception...{0}", ex.Message));
						this.OnErrorEvent(this, new ErrorEventArgs("SocketIO.Client.Connect threw an exception", ex));
					}
				}
			}
		}
		public IEndPointClient Connect(string endPoint)
		{
			EndPointClient nsClient = new EndPointClient(this, endPoint);
			this.Connect();
			this.Send(new ConnectMessage(endPoint));
			return nsClient;
		}

		protected void ReConnect()
		{
			this.retryConnectionCount++;

			this.OnConnectionRetryAttemptEvent(this, EventArgs.Empty);

			this.closeHeartBeatTimer(); // stop the heartbeat time
			this.closeWebSocketClient();// stop websocket

			this.Connect();

			bool connected = this.ConnectionOpenEvent.WaitOne(4000); // block while waiting for connection
			Trace.WriteLine(string.Format("\tRetry-Connection successful: {0}", connected));
			if (connected)
				this.retryConnectionCount = 0;
			else
			{	// we didn't connect - try again until exhausted
				if (this.retryConnectionCount < this.RetryConnectionAttempts)
				{
					this.ReConnect();
				}
				else
				{
					this.Close();
					this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
				}
			}
		}
		
		/// <summary>
		/// <para>Asynchronously calls the action delegate on event message notification</para>
		/// <para>Mimicks the Socket.IO client 'socket.on('name',function(data){});' pattern</para>
		/// <para>Reserved socket.io event names available: connect, disconnect, open, close, error, retry, reconnect  </para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="action"></param>
		/// <example>
		/// client.On("testme", (data) =>
		///    {
		///        Debug.WriteLine(data.ToJson());
		///    });
		/// </example>
		public virtual void On(
			string eventName,
			Action<IMessage> action)
		{
			this.registrationManager.AddOnEvent(eventName, action);
		}
		public virtual void On(
			string eventName,
			string endPoint,
			Action<IMessage> action)
		{
			
			this.registrationManager.AddOnEvent(eventName, endPoint, action);
		}
		/// <summary>
		/// <para>Asynchronously sends payload using eventName</para>
		/// <para>payload must a string or Json Serializable</para>
		/// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
		/// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="payload">must be a string or a Json Serializable object</param>
		/// <remarks>ArgumentOutOfRangeException will be thrown on reserved event names</remarks>
		public void Emit(string eventName, Object payload, string endPoint , Action<Object>  callback)
		{

			string lceventName = eventName.ToLower();
			IMessage msg = null;
			switch (lceventName)
			{
				case "message":
					if (payload is string)
						msg = new TextMessage() { MessageText = payload.ToString() };
					else
						msg = new JSONMessage((Hashtable)payload);
					this.Send(msg);
					break;
				case "connect":
				case "disconnect":
				case "open":
				case "close":
				case "error":
				case "retry":
				case "reconnect":
					throw new System.ArgumentOutOfRangeException(eventName, "Event name is reserved by socket.io, and cannot be used by clients or servers with this message type");
				default:
					if (!string.IsNullOrEmpty(endPoint) && !endPoint.StartsWith("/"))
						endPoint = "/" + endPoint;
					msg = new EventMessage(eventName, payload, endPoint, callback);
					if (callback != null)
						this.registrationManager.AddCallBack(msg);

					this.Send(msg);
					break;
			}
		}

		/// <summary>
		/// <para>Asynchronously sends payload using eventName</para>
		/// <para>payload must a string or Json Serializable</para>
		/// <para>Mimicks Socket.IO client 'socket.emit('name',payload);' pattern</para>
		/// <para>Do not use the reserved socket.io event names: connect, disconnect, open, close, error, retry, reconnect</para>
		/// </summary>
		/// <param name="eventName"></param>
		/// <param name="payload">must be a string or a Json Serializable object</param>
		public void Emit(string eventName, Object payload)
		{
			this.Emit(eventName, payload, string.Empty, null);
		}

		/// <summary>
		/// Queue outbound message
		/// </summary>
		/// <param name="msg"></param>
		public void Send(IMessage msg)
		{
			this.MessageQueueEmptyEvent.Reset();
			if (this.outboundQueue != null)
				this.outboundQueue.Enqueue(msg.Encoded);
		}
		
		public void Send(string msg) {
			IMessage message = new TextMessage() { MessageText =  msg };
			Send(message);
		}

		private void Send_backup(string rawEncodedMessageText)
		{
			this.MessageQueueEmptyEvent.Reset();
			if (this.outboundQueue != null)
				this.outboundQueue.Enqueue(rawEncodedMessageText);
		}

		/// <summary>
		/// if a registerd event name is found, don't raise the more generic Message event
		/// </summary>
		/// <param name="msg"></param>
		protected void OnMessageEvent(IMessage msg)
		{

			bool skip = false;
			if (!string.IsNullOrEmpty(msg.Event))
				skip = this.registrationManager.InvokeOnEvent(msg); // 

			var handler = this.Message;
			if (handler != null && !skip)
			{
				//UnityEngine.Debug.Log(string.Format("webSocket_OnMessage: {0}", msg.RawMessage));
				handler(this, new MessageEventArgs(msg));
			}
		}
		
		/// <summary>
		/// Close SocketIO4Net.Client and clear all event registrations 
		/// </summary>
		public void Close()
		{
			this.retryConnectionCount = 0; // reset for next connection cycle
			// stop the heartbeat time
			this.closeHeartBeatTimer();

			// stop outbound messages
			this.closeOutboundQueue();

			this.closeWebSocketClient();

			if (this.registrationManager != null)
			{
				this.registrationManager.Dispose();
				this.registrationManager = null;
			}

		}

		protected void closeHeartBeatTimer()
		{
			// stop the heartbeat timer
			if (this.socketHeartBeatTimer != 0)
			{
                DelayedExecutorComponent.GetInstance().Cancel(socketHeartBeatTimer);
                socketHeartBeatTimer = 0;
                /*
				this.socketHeartBeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
				this.socketHeartBeatTimer.Dispose();
				this.socketHeartBeatTimer = null;
                */
			}
		}
		protected void closeOutboundQueue()
		{
			// stop outbound messages
			if (this.outboundQueue != null)
			{
				//this.outboundQueue.TryDequeue(); // stop adding any more items;
				//this.dequeuOutBoundMsgTask.Wait(700); // wait for dequeue thread to stop
				//this.outboundQueue = n
				this.outboundQueue = null;
			}
		}
		protected void closeWebSocketClient()
		{
			if (this.wsClient != null)
			{
				// unwire events
				/*
                this.wsClient.Closed -= this.wsClient_Closed;
				this.wsClient.MessageReceived -= wsClient_MessageReceived;
				this.wsClient.Error -= wsClient_Error;
				this.wsClient.Opened -= this.wsClient_OpenEvent;
                */

				if (this.wsClient.State == WebSocketState.Connecting || this.wsClient.State == WebSocketState.Open)
				{
					try { this.wsClient.Close(); }
					catch { Trace.WriteLine("exception raised trying to close websocket: can safely ignore, socket is being closed"); }
				}
				this.wsClient = null;
			}
		}

		// websocket client events - open, messages, errors, closing
		private void wsClient_OpenEvent(IWebsocket socket)
		{
            UnityEngine.Debug.LogWarning("wsClient_OpenEvent");
            UnityEngine.Debug.Log(string.Format("Heartbeat every {0} seconds", HandShake.HeartbeatInterval));
            this.socketHeartBeatTimer =
                DelayedExecutorComponent.GetInstance().Execute(OnHeartBeatTimerCallback, HandShake.HeartbeatInterval);
                // new Timer(OnHeartBeatTimerCallback, new object(), HandShake.HeartbeatInterval, HandShake.HeartbeatInterval);
			
            this.ConnectionOpenEvent.Set();

			this.OnMessageEvent(new EventMessage() { Event = "open" });
			if (this.Opened != null)
			{
				try { this.Opened(this, EventArgs.Empty); }
				catch (Exception ex) { Trace.WriteLine(ex); }
			}

		}

		/// <summary>
		/// Raw websocket messages from server - convert to message types and call subscribers of events and/or callbacks
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void wsClient_MessageReceived(IWebsocket socket, string data)
		{
			IMessage iMsg = SocketIOClient.Messages.Message.Factory(data);
            //UnityEngine.Debug.Log(string.Format("wsClient_MessageReceived: {0}", iMsg));

            if (iMsg.Event == "responseMsg")
				Trace.WriteLine(string.Format("InvokeOnEvent: {0}", iMsg.RawMessage));
			switch (iMsg.MessageType)
			{
				case SocketIOMessageTypes.Disconnect:
					this.OnMessageEvent(iMsg);
					if (string.IsNullOrEmpty(iMsg.Endpoint)) // Disconnect the whole socket
						this.Close();
					break;
				case SocketIOMessageTypes.Heartbeat:
					this.OnHeartBeatTimerCallback();
					break;
				case SocketIOMessageTypes.Connect:
				case SocketIOMessageTypes.Message:
				case SocketIOMessageTypes.JSONMessage:
				case SocketIOMessageTypes.Event:
				case SocketIOMessageTypes.Error:
					this.OnMessageEvent(iMsg);
					break;
				case SocketIOMessageTypes.ACK:
					this.registrationManager.InvokeCallBack(iMsg.AckId, iMsg.Json);
					break;
				default:
					Trace.WriteLine("unknown wsClient message Received...");
					break;
			}
		}

		/// <summary>
		/// websocket has closed unexpectedly - retry connection
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void wsClient_Closed(IWebsocket socket, ushort code, string reason)
		{
			if (this.retryConnectionCount < this.RetryConnectionAttempts   )
			{
				this.ConnectionOpenEvent.Reset();
				this.ReConnect();
			}
			else
			{
				this.Close();
				this.OnSocketConnectionClosedEvent(this, EventArgs.Empty);
			}
		}

		private void wsClient_Error(IWebsocket socket, string message)
		{
			this.OnErrorEvent(socket, new ErrorEventArgs("SocketClient error", new System.Exception(message)));
		}

		protected void OnErrorEvent(object sender, ErrorEventArgs e)
		{
			this.LastErrorMessage = e.Message;
			if (this.Error != null)
			{
				try { this.Error.Invoke(this, e); }
				catch { }
			}
			Trace.WriteLine(string.Format("Error Event: {0}\r\n\t{1}", e.Message, e.Exception));
		}
		protected void OnSocketConnectionClosedEvent(object sender, EventArgs e)
		{
			if (this.SocketConnectionClosed != null)
				{
					try { this.SocketConnectionClosed(sender, e); }
					catch { }
				}
			UnityEngine.Debug.Log("SocketConnectionClosedEvent");
		}
		protected void OnConnectionRetryAttemptEvent(object sender, EventArgs e)
		{
			if (this.ConnectionRetryAttempt != null)
			{
				try { this.ConnectionRetryAttempt(sender, e); }
				catch (Exception ex) { Trace.WriteLine(ex); }
			}
			UnityEngine.Debug.Log(string.Format("Attempting to reconnect: {0}", this.retryConnectionCount));
		}

		// Housekeeping
		protected void OnHeartBeatTimerCallback(/*object state*/)
		{
			if (this.ReadyState == WebSocketState.Open)
			{
                //UnityEngine.Debug.Log("Heartbeat");
                IMessage msg = new Heartbeat();
				try
				{
					if (this.outboundQueue != null)
					{
						this.outboundQueue.Enqueue(msg.Encoded);
						if (this.HeartBeatTimerEvent != null)
						{
							this.HeartBeatTimerEvent.BeginInvoke(this, EventArgs.Empty, EndAsyncEvent, null);
						}
					}
				}
				catch(Exception ex)
				{
					// 
					Trace.WriteLine(string.Format("OnHeartBeatTimerCallback Error Event: {0}\r\n\t{1}", ex.Message, ex.InnerException));
				}
                // send again
                this.socketHeartBeatTimer =
                   DelayedExecutorComponent.GetInstance().Execute(OnHeartBeatTimerCallback, HandShake.HeartbeatInterval);
            }
        }
		private void EndAsyncEvent(IAsyncResult result)
		{
			var ar = (System.Runtime.Remoting.Messaging.AsyncResult)result;
			var invokedMethod = (EventHandler)ar.AsyncDelegate;

			try
			{
				invokedMethod.EndInvoke(result);
			}
			catch
			{
				// Handle any exceptions that were thrown by the invoked method
				Trace.WriteLine("An event listener went kaboom!");
			}
		}
		/// <summary>
		/// While connection is open, dequeue and send messages to the socket server
		/// </summary>
		protected IEnumerator DequeuOutboundMessages()
		{
			while (this.outboundQueue != null)
			{
				if (this.ReadyState == WebSocketState.Open)
				{
                    if (outboundQueue.Count > 0)
                    {
                        try
                        {
                            this.wsClient.Send(outboundQueue.Dequeue());
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine("The outboundQueue is no longer open...");
                        }
                    }
                    else
                    {
                        this.MessageQueueEmptyEvent.Set();
                        yield return 0;
                    }					
				}
				else
				{
                    yield return new UnityEngine.WaitForSeconds(2);
					//this.ConnectionOpenEvent.WaitOne(2000); // wait for connection event
				}
			}
		}

		/// <summary>
		/// <para>Client performs an initial HTTP POST to obtain a SessionId (sid) assigned to a client, followed
		///  by the heartbeat timeout, connection closing timeout, and the list of supported transports.</para>
		/// <para>The tansport and sid are required as part of the ws: transport connection</para>
		/// </summary>
		/// <param name="uri">http://localhost:3000</param>
		/// <returns>Handshake object with sid value</returns>
		/// <example>DownloadString: 13052140081337757257:15:25:websocket,htmlfile,xhr-polling,jsonp-polling</example>
		protected IEnumerator requestHandshake(Uri uri, Action<SocketIOHandshake> callback)
		{
			string value = string.Empty;
			string errorText = string.Empty;
			SocketIOHandshake handshake = null;

            var request = new UnityEngine.WWW(string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port, uri.Query));
            yield return request;
            /*UnityHTTP.Request request = new UnityHTTP.Request("get", string.Format("{0}://{1}:{2}/socket.io/1/{3}", uri.Scheme, uri.Host, uri.Port, uri.Query));
			request.Send(req => {
                */
           	if (request.error == null) {
					value = request.text;
				}
				if (string.IsNullOrEmpty(value))
					errorText = "Did not receive handshake string from server";
				if (string.IsNullOrEmpty(errorText))
					handshake = SocketIOHandshake.LoadFromString(value);
				else
				{
					handshake = new SocketIOHandshake();
					handshake.ErrorMessage = errorText;
				}
				callback(handshake);
			//});
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		// The bulk of the clean-up code 
		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				// free managed resources
				this.Close();
				this.MessageQueueEmptyEvent.Close();
				this.ConnectionOpenEvent.Close();
			}
			
		}
	}


}
