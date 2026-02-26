using UnityEngine;
using System.Collections;
using PassthroughCameraSamples;
using Meta.XR;


[RequireComponent(typeof(Renderer))]
public class WebCamTextureAssinger : MonoBehaviour
{
    IEnumerator Start()
    {
        PassthroughCameraAccess webCamTextureManager = null;
        WebCamTexture webCamTexture = null;

        do
        {
            yield return null;

            if(webCamTextureManager == null)
            {
                webCamTextureManager = FindFirstObjectByType<PassthroughCameraAccess>();
            }
            else
            {
                webCamTexture = webCamTextureManager.GetTexture() as WebCamTexture;
            }
        }while (webCamTexture == null);

        GetComponent<Renderer>().material.mainTexture = webCamTexture;
    }
}
