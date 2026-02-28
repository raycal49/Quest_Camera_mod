using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.WebRTC;

public class NegotiateResponse
{
    [JsonProperty("url")]
    public string Url { get; set; }
}

public class IceConfig
{
    [JsonProperty("iceServers")]
    public IceServerData[] IceServers { get; set; }
}

public class IceServerData
{
    [JsonProperty("urls")]
    public string[] Urls { get; set; }

    [JsonProperty("username")]
    public string Username { get; set; }

    [JsonProperty("credential")]
    public string Credential { get; set; }
}


/// outgoing message dtos

public class JoinGroupMessage
{
    [JsonProperty("type")]
    public string Type { get; set; } = "joinGroup";

    [JsonProperty("group")]
    public string Group { get; set; }
}

public class SendToGroupMessage<TData>
{
    [JsonProperty("type")]
    public string Type { get; set; } = "sendToGroup";

    [JsonProperty("group")]
    public string Group { get; set; } = "lobby";

    [JsonProperty("dataType")]
    public string DataType { get; set; } = "json";

    [JsonProperty("data")]
    public TData Data { get; set; }
}

public class CallRequestData
{
    [JsonProperty("type")]
    public string Type { get; set; } = "call-request";

    [JsonProperty("room")]
    public string Room { get; set; }

    [JsonProperty("callerName")]
    public string CallerName { get; set; }
}


public class CallEndedData
{
    [JsonProperty("type")]
    public string Type { get; set; } = "call-ended";

    [JsonProperty("room")]
    public string Room { get; set; }
}

public class OfferMessageData
{
    [JsonProperty("type")]
    public string Type { get; set; } = "offer";

    [JsonProperty("room")]
    public string Room { get; set; }

    [JsonProperty("sdp")]
    public string Sdp { get; set; }
}

public class IceCandidateRequestData
{
    [JsonProperty("type")]
    public string Type { get; set; } = "ice-candidate";

    [JsonProperty("room")]
    public string Room { get; set; }

    [JsonProperty("candidate")]
    public IceCandidatePayload Candidate { get; set; }
}


// incoming message dtos

public sealed class WebPubSubEnvelope<T>
{
    [JsonProperty("type")]
    public string Type { get; set; } // "message", "system", "ack", ...

    [JsonProperty("from")]
    public string From { get; set; } // "group" or "server"

    [JsonProperty("fromUserId")]
    public string FromUserId { get; set; }

    [JsonProperty("group")]
    public string Group { get; set; }

    [JsonProperty("dataType")]
    public string DataType { get; set; } // "json"

    [JsonProperty("data")]
    public T Data { get; set; } // <-- your payload
}

public sealed class SignalingMessage
{
    [JsonProperty("type")]
    public string Type { get; set; } // "call-accepted", "answer", ...

    [JsonProperty("room")]
    public string Room { get; set; }

    [JsonProperty("callerName")]
    public string CallerName { get; set; }

    [JsonProperty("sdp")]
    public string Sdp { get; set; } // offer/answer only

    [JsonProperty("candidate")]
    public IceCandidatePayload Candidate { get; set; } // ice-candidate only
}

public class IceCandidatePayload
{
    [JsonProperty("candidate")]
    public string Candidate { get; set; }

    [JsonProperty("sdpMid")]
    public string SdpMid { get; set; }

    [JsonProperty("sdpMLineIndex")]
    public int SdpMLineIndex { get; set; }
}