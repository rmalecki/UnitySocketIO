using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Collections;
//using SimpleJson.Reflection;

namespace SocketIOClient.Messages
{
    public class JsonEncodedEventMessage
    {
         public string Name { get; set; }

         public object[] Args { get; set; }

        public JsonEncodedEventMessage()
        {
        }
        
		public JsonEncodedEventMessage(string name, object payload) : this(name, new[]{payload})
        {

        }
        
		public JsonEncodedEventMessage(string name, object[] payloads)
        {
            this.Name = name;
            this.Args = payloads;
        }

        public T GetFirstArgAs<T>() where T : new()
        {
            try
            {
                var firstArg = this.Args.FirstOrDefault();
                if (firstArg != null)
                    //return SimpleJson.SimpleJson.DeserializeObject<T>(firstArg.ToString());
					return JsonHelper<T>.objectFromJson((Hashtable)JSON.JsonDecode(firstArg.ToString()));
            }
            catch (Exception ex)
            {
                // add error logging here
                throw;
            }
            return default(T);
        }
        public IEnumerable<T> GetArgsAs<T>() where T : new()
        {
            List<T> items = new List<T>();
            foreach (var i in this.Args)
            {
                //items.Add( SimpleJson.SimpleJson.DeserializeObject<T>(i.ToString()) );
				items.Add(JsonHelper<T>.objectFromJson((Hashtable)JSON.JsonDecode(i.ToString())));
            }
            return items.AsEnumerable();
        }

        public string ToJsonString()
        {
            //return SimpleJson.SimpleJson.SerializeObject(this);
			return JSON.JsonEncode(this);
        }

        public static JsonEncodedEventMessage Deserialize(string jsonString)
        {
			//UnityEngine.Debug.Log(jsonString);
			JsonEncodedEventMessage msg = null;
			//try { msg = SimpleJson.SimpleJson.DeserializeObject<JsonEncodedEventMessage>(jsonString); }
			try { msg = JsonHelper<JsonEncodedEventMessage>.objectFromJson((Hashtable)JSON.JsonDecode(jsonString)); }
			catch (Exception ex)
			{
				Trace.WriteLine(ex);
			}
            return msg;
        }
    }
}
