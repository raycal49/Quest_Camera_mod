using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;

public class WebRtcPeerConnectionClient : IDisposable
{
    private RTCPeerConnection _peerConnection;

    public event Action<IceCandidatePayload> IceCandidateReady;
    public event Action<RTCPeerConnectionState> ConnectionStateChanged;
    public event Action<string> OfferReady;

    public void SetupPeerConnection(IceConfig iceConfig)
    {
        var iceServers = new List<RTCIceServer>();
        foreach (var server in iceConfig.IceServers)
        {
            iceServers.Add(new RTCIceServer
            {
                urls = server.Urls,
                username = server.Username,
                credential = server.Credential
            });
        }

        var config = new RTCConfiguration
        {
            iceServers = iceServers.ToArray()
        };

        _peerConnection = new RTCPeerConnection(ref config);

        _peerConnection.OnIceCandidate = candidate =>
        {
            if (string.IsNullOrEmpty(candidate.Candidate))
            {
                return;
            }

            IceCandidateReady?.Invoke(new IceCandidatePayload
            {
                Candidate = candidate.Candidate,
                SdpMid = candidate.SdpMid,
                SdpMLineIndex = candidate.SdpMLineIndex ?? 0
            });
        };

        _peerConnection.OnIceConnectionChange = state => Debug.Log($"WebRTC ICE: {state}");
        _peerConnection.OnConnectionStateChange = state =>
        {
            ConnectionStateChanged?.Invoke(state);
        };
    }

    public void AddTrack(VideoStreamTrack videoTrack)
    {
        _peerConnection?.AddTrack(videoTrack);
    }

    public IEnumerator SendOffer(VideoStreamTrack videoTrack)
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
            //Debug.LogError("WebRTC: Video track never became live!");
            yield break;
        }

        yield return new WaitForSeconds(0.5f);

        var createOfferOperation = _peerConnection.CreateOffer();
        yield return createOfferOperation;

        var offer = createOfferOperation.Desc;

        var setLocalOperation = _peerConnection.SetLocalDescription(ref offer);
        yield return setLocalOperation;

        OfferReady?.Invoke(offer.sdp);

        //Debug.Log("Offer created and local description set");
    }

    public IEnumerator SetRemoteAnswer(string sdp)
    {
        var answer = new RTCSessionDescription
        {
            type = RTCSdpType.Answer,
            sdp = sdp
        };

        var setRemoteOperation = _peerConnection.SetRemoteDescription(ref answer);
        yield return setRemoteOperation;
    }

    public void HandleIceCandidate(IceCandidatePayload ice)
    {
        try
        {
            if (_peerConnection == null || ice == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(ice.Candidate))
            {
                Debug.Log("WebRTC: End of candidates signal received, ignoring.");
                return;
            }

            var iceCandidate = new RTCIceCandidateInit
            {
                candidate = ice.Candidate,
                sdpMid = ice.SdpMid ?? "0",
                sdpMLineIndex = ice.SdpMLineIndex
            };

            _peerConnection.AddIceCandidate(new RTCIceCandidate(iceCandidate));
            //Debug.Log("WebRTC: ICE candidate added");
        }
        catch (Exception exception)
        {
            //Debug.LogError("WebRTC: ICE candidate error: " + exception.Message);
        }
    }

    public void Dispose()
    {
        _peerConnection?.Close();
    }
}
