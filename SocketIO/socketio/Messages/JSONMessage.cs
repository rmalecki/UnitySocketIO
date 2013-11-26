using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;
 

namespace SocketIOClient.Messages
{
    public class JSONMessage : Message
    {
        public void SetMessage(object value)
        {
            //this.MessageText = SimpleJson.SimpleJson.SerializeObject(value);
			this.MessageText = JSON.JsonEncode(value);
			//UnityEngine.Debug.Log("JSONMessage:SetMessage: " + this.MessageText);
        }

        public virtual T Message<T>() where T : new()
        {
			//UnityEngine.Debug.Log("JSONMessage:Message: " + this.MessageText);
            //try { return SimpleJson.SimpleJson.DeserializeObject<T>(this.MessageText); }
			try { return JsonHelper<T>.objectFromJson((Hashtable)JSON.JsonDecode(this.MessageText)); }
            catch (Exception ex)
            {
                // add error logging here
                throw;
            }
        }

        public JSONMessage()
        {
            this.MessageType = SocketIOMessageTypes.JSONMessage;
        }
		 public JSONMessage(object jsonObject):this()
        {
   
            //this.MessageText = SimpleJson.SimpleJson.SerializeObject(jsonObject );
			this.MessageText = JsonHelper<object>.jsonFromObject(jsonObject);
			//UnityEngine.Debug.Log("JSONMessage: " + jsonObject + " = " + this.MessageText);
        }
		
        public JSONMessage(object jsonObject, int? ackId  , string endpoint ):this()
        {
            this.AckId = ackId;
            this.Endpoint = endpoint;
            //this.MessageText = SimpleJson.SimpleJson.SerializeObject(jsonObject );
			this.MessageText = JsonHelper<object>.jsonFromObject(jsonObject);
			//UnityEngine.Debug.Log("JSONMessage: " + jsonObject + " = " + this.MessageText);
        }

        public static JSONMessage Deserialize(string rawMessage)
        {
			//UnityEngine.Debug.Log("JSONMessage:Deserialize: " + rawMessage);
			JSONMessage jsonMsg = new JSONMessage();
            //  '4:' [message id ('+')] ':' [message endpoint] ':' [json]
            //   4:1::{"a":"b"}
			jsonMsg.RawMessage = rawMessage;

            string[] args = rawMessage.Split(SPLITCHARS, 4); // limit the number of '
            if (args.Length == 4)
            {
                int id;
                if (int.TryParse(args[1], out id))
					jsonMsg.AckId = id;
				jsonMsg.Endpoint = args[2];
				jsonMsg.MessageText = args[3];
            }
			return jsonMsg;
        }
    }
}
