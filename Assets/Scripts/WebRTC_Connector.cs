using Meta.XR;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Android;
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

    private WebRtcPeerConnectionClient peerConnectionClient;
    private WebRtcController sessionController;
    private VideoStreamTrack videoTrack;
    private bool _trackReady = false;

    //private string signalingServerUrl = "https://ar-signaling-server.azurewebsites.net";
    private AzureApiClient server = new AzureApiClient("https://ar-signaling-server.azurewebsites.net");
    private string userId;
    private string room;

    //private ClientWebSocket ws;
    public Socket socket = new Socket();
    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private bool _loggedMissingSessionController = false;

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
        if (sessionController == null)
        {
            return;
        }

        while (messageQueue.TryDequeue(out string message))
        {
            sessionController.HandleIncomingMessage(message);
        }
    }

    void OnSessionCoroutineRequested(IEnumerator coroutine)
    {
        StartCoroutine(coroutine);
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

        peerConnectionClient = new WebRtcPeerConnectionClient();
        peerConnectionClient.SetupPeerConnection(iceConfig);

        sessionController = new WebRtcController(userId, room, socket, peerConnectionClient);
        sessionController.CoroutineRequested += OnSessionCoroutineRequested;
        sessionController.BindPeerEvents();

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

        // why do we have AddTrack and SetVideoTrack, each on two different classes?
        peerConnectionClient.AddTrack(videoTrack);
        sessionController.SetVideoTrack(videoTrack);

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

    
    async Task ConnectWebSocket(string url)
    {
        socket.SetUrl(url);

        await socket.ConnectWebSocket(url);
        Debug.Log("Connected to Web PubSub");

        await sessionController.JoinRoom();

        await Task.Delay(2000);

        await sessionController.SendCallRequest(callerName);

        // Start receiving which will enqueue a message and eventually Update() will call and execute HandleMessage then we hope to receive a message back
        await socket.ReceiveLoop(messageQueue);
    }

    void OnDestroy()
    {
        videoTrack?.Dispose();
        socket?.Dispose();

        if (sessionController != null)
        {
            sessionController.CoroutineRequested -= OnSessionCoroutineRequested;
            sessionController.UnbindPeerEvents();
        }

        if (_webRtcRenderTexture != null) Destroy(_webRtcRenderTexture);
    }
}