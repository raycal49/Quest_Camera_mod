using Newtonsoft.Json;
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

///
/// 

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

    [JsonProperty("group")]
    public string Group { get; set; }

    [JsonProperty("callerName")]
    public string CallerName { get; set; }
}


public class CallEndedData
{
    [JsonProperty("type")]
    public string Type { get; set; } = "call-ended";

    [JsonProperty("group")]
    public string Group { get; set; }
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

    [JsonProperty("group")]
    public string Group { get; set; }

    [JsonProperty("candidate")]
    public IceCandidatePayload Candidate { get; set; }
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