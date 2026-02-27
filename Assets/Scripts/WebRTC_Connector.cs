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
    [SerializeField] private PassthroughCameraAccess passthroughLeft;
    [SerializeField] private PassthroughCameraAccess passthroughRight;

    [Header("Compositor Settings")]
    [SerializeField] private Camera LeftCaptureCamera;
    [SerializeField] private Camera RightCaptureCamera;

    [Header("Render Textures")]
    [SerializeField] private RenderTexture LeftCameraRT;
    [SerializeField] private RenderTexture RightCameraRT;
    private RenderTexture _webRtcRenderTexture;
    private Material _stereoBlendMaterial;

    private string callerName = "Meta Quest User";


    private RTCPeerConnection peerConnection;
    private VideoStreamTrack videoTrack;
    private bool _trackReady = false;

    

    private string signalingServerUrl = "https://ar-signaling-server.azurewebsites.net";
    private string userId;
    private string room;

    private ClientWebSocket ws;
    private CancellationTokenSource cts;
    private readonly Queue<string> messageQueue = new Queue<string>();

    void Awake()
    {
        // Generate a unique ID for this Quest session.
        // Using a short random suffix keeps logs readable.
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        userId = $"quest-{suffix}";
        room   = $"quest-{suffix}"; // private room = same as userId for clarity
    }
    void Start()
    {
        StartCoroutine(WebRTC.Update());
        StartCoroutine(RequestPermissionThenInit());
    }

    void Update()
    {
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
        Debug.Log($"WebRTC: Initializing as userId={userId}, room={room}");
        
        using var iceReq = UnityWebRequest.Get($"{signalingServerUrl}/ice-config");
        yield return iceReq.SendWebRequest();

        if (iceReq.result != UnityWebRequest.Result.Success)
        {
            //Debug.LogError("ICE config failed: " + iceReq.error);
            yield break;
        }

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
      while ((!passthroughLeft.IsPlaying || !passthroughRight.IsPlaying) && elapsed < timeout)
      {
          yield return new WaitForSeconds(0.2f);
          elapsed += 0.2f;
      }

      if (!passthroughLeft.IsPlaying || !passthroughRight.IsPlaying)
      {
          //Debug.LogError("WebRTC: PassthroughCameraAccess never started playing!");
          yield break;
      }

      yield return new WaitForEndOfFrame();
      yield return new WaitForEndOfFrame();

      var sourceRt = passthroughLeft.GetTexture() as RenderTexture;
      if (sourceRt == null)
      {
          //Debug.LogError("WebRTC: Could not get RenderTexture from PassthroughCameraAccess!");
          yield break;
      }
      _webRtcRenderTexture = new RenderTexture(sourceRt.width, sourceRt.height, 0)
      {
          useMipMap = false,
          autoGenerateMips = false,
          graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB
      };
      _webRtcRenderTexture.Create();

      AlignCaptureCamerasToLens();

      var shader = Shader.Find("Custom/StereoBlend");
        if (shader == null)
        {
            //Debug.LogError("WebRTC: Could not find shader Custom/StereoBlend!");
            yield break;
        }
        _stereoBlendMaterial = new Material(shader);

      videoTrack = new VideoStreamTrack(_webRtcRenderTexture);
      peerConnection.AddTrack(videoTrack);

      _trackReady = true;
      StartCoroutine(CompositorLoop());

      //Debug.Log("WebRTC: Track created, starting loop");
    }

    IEnumerator CompositorLoop()
    {
        //Debug.Log("WebRTC: CompositorLoop started");
        while (true)
        {
            yield return new WaitForEndOfFrame();

            if (!_trackReady || _webRtcRenderTexture == null) continue;
            if (!passthroughLeft.IsPlaying || !passthroughRight.IsPlaying) {
            //Debug.Log($"WebRTC: Passthrough not playing — left={passthroughLeft.IsPlaying}, right={passthroughRight.IsPlaying}");
            continue;
            }

            var leftSrc  = passthroughLeft.GetTexture();
            var rightSrc = passthroughRight.GetTexture();
            if (leftSrc == null || rightSrc == null) continue;

            _stereoBlendMaterial.SetTexture("_LeftTex",     leftSrc);
            _stereoBlendMaterial.SetTexture("_RightTex",    rightSrc);
            _stereoBlendMaterial.SetTexture("_LeftAssets",  LeftCameraRT);
            _stereoBlendMaterial.SetTexture("_RightAssets", RightCameraRT);

            Graphics.Blit(null, _webRtcRenderTexture, _stereoBlendMaterial);
            //Debug.Log("WebRTC: Blit complete");
        }
    }
    void AlignCaptureCamerasToLens()
    {
        // Use the physical lens offsets from the SDK so Unity cameras
        // precisely match the real-world passthrough camera positions
        var leftLens  = passthroughLeft.Intrinsics.LensOffset;
        var rightLens = passthroughRight.Intrinsics.LensOffset;

        LeftCaptureCamera.transform.localPosition  = leftLens.position;
        LeftCaptureCamera.transform.localRotation  = leftLens.rotation;
        RightCaptureCamera.transform.localPosition = rightLens.position;
        RightCaptureCamera.transform.localRotation = rightLens.rotation;

        // Derive vertical FOV from the camera intrinsics
        float fovLeft  = 2f * Mathf.Atan(passthroughLeft.CurrentResolution.y  / (2f * passthroughLeft.Intrinsics.FocalLength.y))  * Mathf.Rad2Deg;
        float fovRight = 2f * Mathf.Atan(passthroughRight.CurrentResolution.y / (2f * passthroughRight.Intrinsics.FocalLength.y)) * Mathf.Rad2Deg;

        LeftCaptureCamera.fieldOfView  = fovLeft;
        RightCaptureCamera.fieldOfView = fovRight;
    }

    async Task ConnectWebSocket(string url)
    {
        ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("json.webpubsub.azure.v1");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        //Debug.Log("Connected to Web PubSub");

        // Join room
        SendWs(new {
            type = "joinGroup",
            group = room
        });

        await Task.Delay(2000);

        // Send call request
        SendWs(new {
            type = "sendToGroup",
            group = "lobby",
            dataType = "json",
            data = new { type = "call-request", room, callerName }
        });
        Debug.Log($"WebRTC: Call request sent to lobby for room={room}");

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
        //Debug.Log("WebRTC WS <- " + raw);

        var msg = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
        if (!msg.ContainsKey("data")) return;

        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(
            msg["data"].ToString());

        if (!data.ContainsKey("type")) return;
        var type = data["type"].ToString();

        // Only process messages intended for our room
        if (data.ContainsKey("room") && data["room"]?.ToString() != room)
            return;

        switch (type)
        {
            case "call-accepted":
                Debug.Log("Call accepted — sending offer");
                StartCoroutine(SendOffer());
                break;

            case "call-declined":
                Debug.Log("WebRTC: Call declined by web client");
                // Optionally: re-broadcast to lobby after a delay so other web clients can answer
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
                //Debug.Log("WebRTC: End of candidates signal received, ignoring.");
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
            //Debug.Log("WebRTC: ICE candidate added");
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
        peerConnection.OnConnectionStateChange = state =>
        {
            Debug.Log($"WebRTC Connection: {state}");
            if (state == RTCPeerConnectionState.Disconnected || state == RTCPeerConnectionState.Failed)
            {
                // Notify lobby that this call ended so web clients can clean up
                SendWs(new {
                    type     = "sendToGroup",
                    group    = "lobby",
                    dataType = "json",
                    data     = new { type = "call-ended", room }
                });
            }
        };

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
        //Debug.Log("Offer sent");
    }

    IEnumerator SetRemoteAnswer(string sdp)
    {
        var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
        var op = peerConnection.SetRemoteDescription(ref answer);
        yield return op;
        //Debug.Log("WebRTC connected!");
    }

    void OnDestroy()
    {
        // Notifying lobby that call is ended
        if (ws != null && ws.State == WebSocketState.Open)
        {
            SendWs(new {
                type     = "sendToGroup",
                group    = "lobby",
                dataType = "json",
                data     = new { type = "call-ended", room }
            });
        }

        cts?.Cancel();
        videoTrack?.Dispose();
        peerConnection?.Close();
        ws?.Dispose();
        if (_webRtcRenderTexture != null) Destroy(_webRtcRenderTexture);
        if (LeftCameraRT  != null) Destroy(LeftCameraRT);
        if (RightCameraRT != null) Destroy(RightCameraRT);
    }
}

[Serializable] class NegotiateResponse { public string url; }
[Serializable] class IceConfigResponse { public IceServerData[] iceServers; }
[Serializable] class IceServerData { public string[] urls; public string username; public string credential; }