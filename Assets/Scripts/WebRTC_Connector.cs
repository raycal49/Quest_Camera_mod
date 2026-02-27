using Meta.XR;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using Newtonsoft.Json;

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

    //private string signalingServerUrl = "https://ar-signaling-server.azurewebsites.net";
    private SignalServer server = new SignalServer("https://ar-signaling-server.azurewebsites.net");
    private string userId = "quest-user";
    private string room;

    //private ClientWebSocket ws;
    public Socket socket = new Socket();
    private CancellationTokenSource cts = new CancellationTokenSource();
    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

    void Awake()
    {
        // Generate a unique ID for this Quest session.
        // Using a short random suffix keeps logs readable.
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        userId = $"quest-{suffix}";
        room = $"quest-{suffix}"; // private room = same as userId for clarity
    }

    void Start()
    {
        Debug.Log("WebRTC: Start() called");
        StartCoroutine(WebRTC.Update());
        StartCoroutine(RequestPermissionThenInit());
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string message))
        {
            HandleMessage(message);
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

        yield return server.GetIceConfig();

        var iceConfig = server.IceConfig;

        if (iceConfig == null)
            yield break;

        Debug.Log("WebRTC: Ice config set");

        yield return server.GetNegotiateUrl(userId, room);

        var negotiateUrl = server.NegotiateUrl;

        if (negotiateUrl == null)
            yield break;

        Debug.Log("WebRTC: Negotiate OK");

        SetupPeerConnection(iceConfig);

        yield return StartCoroutine(SetupVideoTrack());

        // 4. Connect WebSocket
        yield return ConnectWebSocket(negotiateUrl);
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
            if (!passthroughLeft.IsPlaying || !passthroughRight.IsPlaying)
            {
                //Debug.Log($"WebRTC: Passthrough not playing — left={passthroughLeft.IsPlaying}, right={passthroughRight.IsPlaying}");
                continue;
            }

            var leftSrc = passthroughLeft.GetTexture();
            var rightSrc = passthroughRight.GetTexture();
            if (leftSrc == null || rightSrc == null) continue;

            _stereoBlendMaterial.SetTexture("_LeftTex", leftSrc);
            _stereoBlendMaterial.SetTexture("_RightTex", rightSrc);
            _stereoBlendMaterial.SetTexture("_LeftAssets", LeftCameraRT);
            _stereoBlendMaterial.SetTexture("_RightAssets", RightCameraRT);

            Graphics.Blit(null, _webRtcRenderTexture, _stereoBlendMaterial);
            //Debug.Log("WebRTC: Blit complete");
        }
    }

    void AlignCaptureCamerasToLens()
    {
        // Use the physical lens offsets from the SDK so Unity cameras
        // precisely match the real-world passthrough camera positions
        var leftLens = passthroughLeft.Intrinsics.LensOffset;
        var rightLens = passthroughRight.Intrinsics.LensOffset;

        LeftCaptureCamera.transform.localPosition = leftLens.position;
        LeftCaptureCamera.transform.localRotation = leftLens.rotation;
        RightCaptureCamera.transform.localPosition = rightLens.position;
        RightCaptureCamera.transform.localRotation = rightLens.rotation;

        // Derive vertical FOV from the camera intrinsics
        float fovLeft = 2f * Mathf.Atan(passthroughLeft.CurrentResolution.y / (2f * passthroughLeft.Intrinsics.FocalLength.y)) * Mathf.Rad2Deg;
        float fovRight = 2f * Mathf.Atan(passthroughRight.CurrentResolution.y / (2f * passthroughRight.Intrinsics.FocalLength.y)) * Mathf.Rad2Deg;

        LeftCaptureCamera.fieldOfView = fovLeft;
        RightCaptureCamera.fieldOfView = fovRight;
    }

    // need to put this in its own controller
    async Task ConnectWebSocket(string url)
    {
        socket.SetUrl(url);

        await socket.ConnectWebSocket(url);
        Debug.Log("Connected to Web PubSub");

        var joinGroup = new JoinGroupMessage { Group = room };

        // Join room
        await socket.Send(joinGroup);

        await Task.Delay(2000);

        // Send call request
        var sendGroup = new SendToGroupMessage<CallRequestData>
        {
            Group = room,
            Data = new CallRequestData
            {
                Group = room,
                CallerName = callerName 
                
            }
        };

        await socket.Send(sendGroup);

        // Start receiving
        await socket.ReceiveLoop(messageQueue);
    }

    void HandleMessage(string raw)
    {
        Debug.Log("WebRTC WS <- " + raw);

        var msg = JsonConvert.DeserializeObject<Dictionary<string, object>>(raw);
        if (!msg.ContainsKey("data")) return;

        var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(
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

            var candidateObj = JsonConvert
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

    // this is only initialized AFTER we have an IceConfig file
    void SetupPeerConnection(IceConfig iceConfig)
    {
        var iceServers = new List<RTCIceServer>();
        foreach (var s in iceConfig.IceServers)
            iceServers.Add(new RTCIceServer
            {
                urls = s.Urls,
                username = s.Username,
                credential = s.Credential
            });

        var config = new RTCConfiguration { iceServers = iceServers.ToArray() };

        peerConnection = new RTCPeerConnection(ref config);

        peerConnection.OnIceCandidate = candidate =>
        {
            if (string.IsNullOrEmpty(candidate.Candidate)) return;

            var message = new SendToGroupMessage<IceCandidateRequestData>
            {
                Group = room,
                Data = new IceCandidateRequestData
                {
                    Group = room,
                    Candidate = new IceCandidatePayload
                    {
                        Candidate = candidate.Candidate,
                        SdpMid = candidate.SdpMid,
                        SdpMLineIndex = candidate.SdpMLineIndex ?? 0
                    }
                }
            };

            socket.Send(message);
        };

        peerConnection.OnIceConnectionChange = state => Debug.Log($"WebRTC ICE: {state}");
        peerConnection.OnConnectionStateChange = state =>
        {
            Debug.Log($"WebRTC Connection: {state}");
            if (state == RTCPeerConnectionState.Disconnected || state == RTCPeerConnectionState.Failed)
            {
                socket.Send(new SendToGroupMessage<CallEndedData>
                {
                    Group = "lobby",
                    Data = new CallEndedData
                    {
                        Group = room
                    }
                });
            }
        };

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

        var offerMessage = new SendToGroupMessage<OfferMessageData>
        {
            Group = room,
            Data = new OfferMessageData
            {
                Room = room,
                Sdp = offer.sdp
            }
        };

        socket.Send(offerMessage);

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
        socket?.Dispose();
        if (_webRtcRenderTexture != null) Destroy(_webRtcRenderTexture);
    }
}