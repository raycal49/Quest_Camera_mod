using System;
using System.Collections;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

// technically, this doesnt need to derive from Monobehaviour despite it having a function that returns an IEnumerator but it still is dependent on Monobehaviour. yeah....
public class SignalServer
{
    private readonly string _serverUrl;

    public IceConfig IceConfig { get; private set; }

    public string NegotiateUrl;

    public SignalServer(string serverUrl)
    {
        _serverUrl = serverUrl;
    }

    // remember this spawns a state machine (SM) basically and we return a pointer to a state on that SM and when we advance as we go about our function or something i think
    // i need to ask about the exact nature of the path $"{_serverUrl}/ice-config", like is it always /ice-config, even if we have a different serverUrl?
    public IEnumerator GetIceConfig()
    {
       using var req = UnityWebRequest.Get($"{_serverUrl}/ice-config");

       yield return req.SendWebRequest();

       // by having yield break we can leave here and have our field SignalServer.IceConfig just be empty so we need to account for this outside
       // could throw an exception but... eh...
       if (req.result != UnityWebRequest.Result.Success)
       {
           Debug.LogError("ICE config failed: " + req.error);
           IceConfig = null;
           yield break;
       }

       IceConfig = JsonConvert.DeserializeObject<IceConfig>(req.downloadHandler.text);
    }

    public IEnumerator GetNegotiateUrl(string userId, string room)
    {
        using var negReq = UnityWebRequest.Get(
            $"{_serverUrl}/negotiate?userId={userId}&room={room}");
        yield return negReq.SendWebRequest();

        if (negReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Negotiate failed: " + negReq.error);
            yield break;
        }

        var negotiateResponse = JsonConvert.DeserializeObject<NegotiateResponse>(negReq.downloadHandler.text);

        NegotiateUrl = negotiateResponse.Url;
    }

}
