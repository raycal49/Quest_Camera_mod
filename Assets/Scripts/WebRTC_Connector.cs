using System.Collections;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Android;
using Meta.XR;
using UnityEngine.Experimental.Rendering;

public class WebRTCSender : MonoBehaviour
{
  [Header("Passthrough Camera")]
[SerializeField] private PassthroughCameraAccess passthroughCamera;

    [Header("Compositor Settings")]
[SerializeField] private Camera captureCamera; // Camera that sees ONLY Unity Assets
private RenderTexture _assetsRT;
private Material _blendMaterial;

    [Header("Capture")]
[SerializeField] private RenderTexture Pass_Render;


    private RTCPeerConnection peerConnection;
    private VideoStreamTrack videoTrack;
    private bool _trackReady = false;

    private RenderTexture _webRtcRenderTexture;
    private RenderTexture _sourceRt;

    private string signalingServerUrl = "https://ar-signaling-server.azurewebsites.net";
    private string userId = "quest-user";
    private string room = "default-room";

    private ClientWebSocket ws;
    private CancellationTokenSource cts;
    private readonly Queue<string> messageQueue = new Queue<string>();

    void Start()
    {
        Debug.Log("WebRTC: Start() called");
        StartCoroutine(WebRTC.Update());
        StartCoroutine(RequestPermissionThenInit());
    }

    void Update()
    {
        if (_trackReady && _webRtcRenderTexture != null && passthroughCamera.IsPlaying)
        {
            var _sourceRt = passthroughCamera.GetTexture();
            if (_sourceRt != null)
            {
                Graphics.Blit(_sourceRt, _webRtcRenderTexture);
                Graphics.Blit(Pass_Render, _webRtcRenderTexture, GetBlendMaterial());
            }
        }
        lock (messageQueue)
        {
            while (messageQueue.Count > 0)
                HandleMessage(messageQueue.Dequeue());
        }
    }

    IEnumerator RequestPermissionThenInit()
    {
        string perm = "horizonos.permission.HEADSET_CAMERA";

        if (!Permission.HasUserAuthorizedPermission(perm))
        {
            bool decided = false;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => { decided = true; Debug.Log("WebRTC: Camera permission granted"); };
            callbacks.PermissionDenied += _ => { decided = true; Debug.LogError("WebRTC: Camera permission denied"); };
            Permission.RequestUserPermission(perm, callbacks);
            yield return new WaitUntil(() => decided);
        }

        if (!Permission.HasUserAuthorizedPermission(perm))
        {
            Debug.LogError("WebRTC: Cannot proceed without camera permission.");
            yield break;
        }

        StartCoroutine(Initialize());
    }

    IEnumerator Initialize()
    {
        Debug.Log("WebRTC: Initialize() started");
        // 1. Fetch ICE config
        using var iceReq = UnityWebRequest.Get($"{signalingServerUrl}/ice-config");
        yield return iceReq.SendWebRequest();

        if (iceReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("ICE config failed: " + iceReq.error);
            yield break;
        }
        Debug.Log("WebRTC: Initialize() started");

        var iceConfig = JsonUtility.FromJson<IceConfigResponse>(iceReq.downloadHandler.text);

        // 2. Negotiate Web PubSub token
        using var negReq = UnityWebRequest.Get(
            $"{signalingServerUrl}/negotiate?userId={userId}&room={room}");
        yield return negReq.SendWebRequest();

        if (negReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Negotiate failed: " + negReq.error);
            yield break;
        }
        Debug.Log("WebRTC: Negotiate OK");

        var negotiateResponse = JsonUtility.FromJson<NegotiateResponse>(negReq.downloadHandler.text);

        // 3. Setup WebRTC
        SetupPeerConnection(iceConfig);

        yield return StartCoroutine(SetupVideoTrack());

        // 4. Connect WebSocket
        cts = new CancellationTokenSource();
        _ = ConnectWebSocket(negotiateResponse.url);
    }

    IEnumerator SetupVideoTrack()
    {
      // Wait until PassthroughCameraAccess is actually playing and has a frame
      float timeout = 10f;
      float elapsed = 0f;
      while (!passthroughCamera.IsPlaying && elapsed < timeout)
      {
          yield return new WaitForSeconds(0.2f);
          elapsed += 0.2f;
      }

      if (!passthroughCamera.IsPlaying)
      {
          Debug.LogError("WebRTC: PassthroughCameraAccess never started playing!");
          yield break;
      }

      yield return new WaitForEndOfFrame();
      yield return new WaitForEndOfFrame();

      var sourceRt = passthroughCamera.GetTexture() as RenderTexture;
      if (sourceRt == null)
      {
          Debug.LogError("WebRTC: Could not get RenderTexture from PassthroughCameraAccess!");
          yield break;
      }

      //Create a WebRTC-compatible RenderTexture (ARGB32 is universally supported)
      _webRtcRenderTexture = new RenderTexture(sourceRt.width, sourceRt.height, 0)
      {
          useMipMap = false,
          autoGenerateMips = false,
          graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB
      };
      _webRtcRenderTexture.Create();

    //   Debug.Log($"WebRTC: Created compatible RT {_webRtcRenderTexture.width}x{_webRtcRenderTexture.height}");
    //   Debug.Log($"WebRTC: RT format is {_webRtcRenderTexture.graphicsFormat}");

        // if (!Pass_Render.IsCreated())
        // {
        //     Pass_Render.Create();
        // }
        
        // _assetsRT = new RenderTexture(Pass_Render.width, Pass_Render.height, 0)
        // {
        //   useMipMap = false,
        //   autoGenerateMips = false,
        //   graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB
        // };
        // _assetsRT.Create();

        // Point your "Assets Only" camera to this buffer
        //captureCamera.targetTexture = _assetsRT;
        //captureCamera.enabled = false; // We will trigger it manually for sync
      //LogSupportedFormats();

      //StartCoroutine(CompositorLoop());

      videoTrack = new VideoStreamTrack(_webRtcRenderTexture);
      peerConnection.AddTrack(videoTrack);

      _trackReady = true;

      // Start the synchronized compositing loop
    
      Debug.Log("WebRTC: Track created, starting loop");
    }

    // IEnumerator CompositorLoop()
    // {
    //     while (true)
    //     {
    //         yield return new WaitForEndOfFrame();

    //         if (passthroughCamera.IsPlaying && _webRtcRenderTexture != null)
    //         {
    //             Graphics.Blit(Pass_Render, _webRtcRenderTexture, GetBlendMaterial());
    //         }
    //     }
    // }

    private Material GetBlendMaterial()
    {
        if (_blendMaterial == null)
        {
            // Internal shader that supports Alpha Blending via code
            _blendMaterial = new Material(Shader.Find("Sprites/Default"));
            _blendMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _blendMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _blendMaterial.SetInt("_ZWrite", 0);
        }
        return _blendMaterial;
    }


    async Task ConnectWebSocket(string url)
    {
        ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("json.webpubsub.azure.v1");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        Debug.Log("Connected to Web PubSub");

        // Join room
        SendWs(new {
            type = "joinGroup",
            group = room
        });

        await Task.Delay(2000);

        // Send call request
        SendWs(new {
            type = "sendToGroup",
            group = room,
            dataType = "json",
            data = new { type = "call-request", room, callerName = "Meta Quest User" }
        });

        // Start receiving
        await ReceiveLoop();
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            lock (messageQueue)
                messageQueue.Enqueue(sb.ToString());
        }
    }

    void SendWs(object data)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        _ = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
    }

    void HandleMessage(string raw)
    {
        Debug.Log("WebRTC WS <- " + raw);

        var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
        if (!msg.ContainsKey("data")) return;

        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(
            msg["data"].ToString());

        if (!data.ContainsKey("type")) return;
        var type = data["type"].ToString();

        switch (type)
        {
            case "call-accepted":
                Debug.Log("Call accepted — sending offer");
                StartCoroutine(SendOffer());
                break;

            case "answer":
                StartCoroutine(SetRemoteAnswer(data["sdp"].ToString()));
                break;

            case "ice-candidate":
                HandleIceCandidate(data);
                break;
        }
    }

    void HandleIceCandidate(Dictionary<string, object> data)
    {
        try
        {
            if (!data.ContainsKey("candidate")) return;
            
            var candidateRaw = data["candidate"];
            if (candidateRaw == null) return;

            var candidateObj = Newtonsoft.Json.JsonConvert
                .DeserializeObject<Dictionary<string, object>>(candidateRaw.ToString());

            if (candidateObj == null) return;

            var candidateStr = candidateObj.ContainsKey("candidate") ? candidateObj["candidate"]?.ToString() : "";
            
            // Empty candidate = end-of-candidates signal, safe to ignore
            if (string.IsNullOrEmpty(candidateStr)) 
            {
                Debug.Log("WebRTC: End of candidates signal received, ignoring.");
                return;
            }

            var sdpMid = candidateObj.ContainsKey("sdpMid") ? candidateObj["sdpMid"]?.ToString() : "0";
            var sdpMLineIndex = candidateObj.ContainsKey("sdpMLineIndex") ?
                int.Parse(candidateObj["sdpMLineIndex"].ToString()) : 0;

            var init = new RTCIceCandidateInit
            {
                candidate = candidateStr,
                sdpMid = sdpMid ?? "0",
                sdpMLineIndex = sdpMLineIndex
            };
            peerConnection.AddIceCandidate(new RTCIceCandidate(init));
            Debug.Log("WebRTC: ICE candidate added");
        }
        catch (Exception e)
        {
            Debug.LogError("WebRTC: ICE candidate error: " + e.Message);
        }
    }

    void SetupPeerConnection(IceConfigResponse iceConfig)
    {
        var iceServers = new List<RTCIceServer>();
        foreach (var s in iceConfig.iceServers)
            iceServers.Add(new RTCIceServer
            {
                urls = s.urls,
                username = s.username,
                credential = s.credential
            });

        var config = new RTCConfiguration { iceServers = iceServers.ToArray() };
        peerConnection = new RTCPeerConnection(ref config);

        // videoTrack = streamCamera.CaptureStreamTrack(1920, 1080);
        // peerConnection.AddTrack(videoTrack);

        peerConnection.OnIceCandidate = candidate =>
        {
            if (string.IsNullOrEmpty(candidate.Candidate)) return;
            SendWs(new {
                type = "sendToGroup",
                group = room,
                dataType = "json",
                data = new {
                    type = "ice-candidate",
                    room,
                    candidate = new {
                        candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid,
                        sdpMLineIndex = candidate.SdpMLineIndex ?? 0
                    }
                }
            });
        };

        peerConnection.OnIceConnectionChange = state => Debug.Log($"WebRTC ICE: {state}");
        peerConnection.OnConnectionStateChange = state => Debug.Log($"WebRTC Connection: {state}");

        Debug.Log("WebRTC: Peer connection created");
    }

    IEnumerator SendOffer()
    {
        float timeout = 10f;
        float elapsed = 0f;
        while ((videoTrack == null || videoTrack.ReadyState != TrackState.Live) && elapsed < timeout)
        {
            yield return new WaitForSeconds(0.2f);
            elapsed += 0.2f;
        }

        if (videoTrack == null || videoTrack.ReadyState != TrackState.Live)
        {
            Debug.LogError("WebRTC: Video track never became live!");
            yield break;
        }

        yield return new WaitForSeconds(0.5f);


        var op = peerConnection.CreateOffer();
        yield return op;
        var offer = op.Desc;
        var setLocal = peerConnection.SetLocalDescription(ref offer);
        yield return setLocal;

        SendWs(new {
            type = "sendToGroup",
            group = room,
            dataType = "json",
            data = new { type = "offer", room, sdp = offer.sdp }
        });
        Debug.Log("Offer sent");
    }

    IEnumerator SetRemoteAnswer(string sdp)
    {
        var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
        var op = peerConnection.SetRemoteDescription(ref answer);
        yield return op;
        Debug.Log("WebRTC connected!");
    }

    void OnDestroy()
    {
        cts?.Cancel();
        videoTrack?.Dispose();
        peerConnection?.Close();
        ws?.Dispose();
        if (_webRtcRenderTexture != null) Destroy(_webRtcRenderTexture);
    }
}

// ── Data classes ──────────────────────────────────────────────────────────────
[Serializable] class NegotiateResponse { public string url; }
[Serializable] class IceConfigResponse { public IceServerData[] iceServers; }
[Serializable] class IceServerData { public string[] urls; public string username; public string credential; }