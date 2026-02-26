using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

public class CaptureCompositor : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess passthroughCamera;
    [SerializeField] private Camera captureCamera;
    [SerializeField] private RenderTexture captureRT; // this is what WebRTC reads

    private RenderTexture _passthroughRT;   // step 1: passthrough frame
    private RenderTexture _assetsRT;         // step 2: unity assets
    private bool _initialized = false;

    void Update()
    {
        if (!passthroughCamera.IsPlaying) return;

        // Sync camera pose every frame
        var pose = passthroughCamera.GetCameraPose();
        captureCamera.transform.SetPositionAndRotation(
            pose.position,
            pose.rotation
        );

        if (!_initialized) Initialize();
    }

    void Initialize()
    {
        int w = captureRT.width;
        int h = captureRT.height;

        // Step 1 RT — holds raw passthrough
        _passthroughRT = new RenderTexture(w, h, 0)
        {
            graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB,
            useMipMap = false,
            autoGenerateMips = false
        };
        _passthroughRT.Create();

        // Step 2 RT — holds unity assets rendered by captureCamera
        _assetsRT = new RenderTexture(w, h, 24)
        {
            graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB,
            useMipMap = false,
            autoGenerateMips = false
        };
        _assetsRT.Create();

        // Point captureCamera at the assets RT
        captureCamera.targetTexture = _assetsRT;
        captureCamera.clearFlags = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = Color.clear; // transparent background

        _initialized = true;
        Debug.Log("CaptureCompositor: Initialized");
    }

    void LateUpdate()
    {
        if (!_initialized || !passthroughCamera.IsPlaying) return;

        // STEP 1 — blit passthrough into captureRT as background
        var sourceTexture = passthroughCamera.GetTexture();
        if (sourceTexture != null)
        {
            Graphics.Blit(sourceTexture, captureRT);
        }

        // STEP 2 — render captureCamera (Unity assets) into _assetsRT
        captureCamera.Render();

        // STEP 3 — composite assets on top of passthrough in captureRT
        // using a transparent blit so only non-transparent pixels show
        Graphics.Blit(_assetsRT, captureRT, GetBlendMaterial());

        // captureRT now has: passthrough background + unity assets on top
        // WebRTC reads captureRT automatically every frame
    }

    private Material _blendMaterial;
    private Material GetBlendMaterial()
    {
        if (_blendMaterial == null)
        {
            _blendMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _blendMaterial.SetFloat("_Surface", 1); // transparent
        }
        return _blendMaterial;
    }

    void OnDestroy()
    {
        if (_passthroughRT != null) Destroy(_passthroughRT);
        if (_assetsRT != null) Destroy(_assetsRT);
        if (_blendMaterial != null) Destroy(_blendMaterial);
    }
}
