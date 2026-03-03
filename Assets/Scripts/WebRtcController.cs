using System;
using System.Collections;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unity.WebRTC;
using UnityEngine;


public class WebRtcController
{
    private readonly string _userId;
    private readonly string _room;
    private readonly Socket _socket;
    private readonly WebRtcPeerConnectionClient _peerConnectionClient;
    private VideoStreamTrack _videoTrack;

    public event Action<IEnumerator> CoroutineRequested;

    public WebRtcController(string userId, string room, Socket socket, WebRtcPeerConnectionClient peerConnectionClient)
    {
        _userId = userId;
        _room = room;
        _socket = socket;
        _peerConnectionClient = peerConnectionClient;
    }

    public void BindPeerEvents()
    {
        _peerConnectionClient.IceCandidateReady += OnIceCandidateReady;
        _peerConnectionClient.ConnectionStateChanged += OnConnectionStateChanged;
        _peerConnectionClient.OfferReady += OnOfferReady;
    }

    public void UnbindPeerEvents()
    {
        _peerConnectionClient.IceCandidateReady -= OnIceCandidateReady;
        _peerConnectionClient.ConnectionStateChanged -= OnConnectionStateChanged;
        _peerConnectionClient.OfferReady -= OnOfferReady;
    }

    // I think JoinRoom, SendCallRequest, and SetVideoTrack should be in a different class...

    public async Task JoinRoom()
    {
        var joinGroup = new JoinGroupMessage
        {
            Group = _room
        };

        await _socket.Send(joinGroup);
    }

    public async Task SendCallRequest(string callerName)
    {
        var sendGroup = new SendToGroupMessage<CallRequestData>
        {
            Group = "lobby",
            Data = new CallRequestData
            {
                Room = _room,
                CallerName = callerName
            }
        };

        await _socket.Send(sendGroup);
    }

    public void SetVideoTrack(VideoStreamTrack videoTrack)
    {
        _videoTrack = videoTrack;
    }

    public void HandleIncomingMessage(string raw)
    {
        Debug.Log("Message passed through contains: " + raw);
        var envelope = JsonConvert.DeserializeObject<WebPubSubEnvelope<SignalingMessage>>(raw);
        if (envelope == null || envelope.Type != "message" || envelope.Data == null)
        {
            return;
        }

        if (envelope.FromUserId == _userId)
        {
            return;
        }

        var data = envelope.Data;
        //Debug.Log("In HandleMessage, data contains: " + data);

        var type = data.Type;
        if (string.IsNullOrEmpty(type)) return;

        //Debug.Log("In HandleMessage, type contains: " + type);

        var sdp = data.Sdp;

        //Debug.Log("In HandleMessage, Sdp contains " + sdp);

        // be careful with this switch statement
        // it originally had offers/answers set like so
        // RequestCoroutine(_peerConnectionClient.SetRemoteAnswer(data.Sdp));
        switch (type)
        {
            case "call-accepted":
                Debug.Log("case: call-accepted");
                RequestCoroutine(_peerConnectionClient.SendOffer(_videoTrack));
                break;

            case "answer":
                Debug.Log("case: answer");
                RequestCoroutine(_peerConnectionClient.SetRemoteAnswer(sdp));
                break;

            case "ice-candidate":
                Debug.Log("case: ice-candidate");
                _peerConnectionClient.HandleIceCandidate(data.Candidate);
                break;

            case "call-declined":
                Debug.Log("WebRTC: Call declined by web client");
                break;
        }
    }

    private void OnIceCandidateReady(IceCandidatePayload candidate)
    {
        var message = new SendToGroupMessage<IceCandidateRequestData>
        {
            Group = _room,
            Data = new IceCandidateRequestData
            {
                Room = _room,
                Candidate = candidate
            }
        };

        _ = _socket.Send(message);
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state)
    {
        if (state != RTCPeerConnectionState.Disconnected && state != RTCPeerConnectionState.Failed)
        {
            return;
        }

        var callEndedMessage = new SendToGroupMessage<CallEndedData>
        {
            Group = "lobby",
            Data = new CallEndedData
            {
                Room = _room
            }
        };

        _ = _socket.Send(callEndedMessage);
    }

    private void OnOfferReady(string offerSdp)
    {
        var offerMessage = new SendToGroupMessage<OfferMessageData>
        {
            Group = _room,
            Data = new OfferMessageData
            {
                Room = _room,
                Sdp = offerSdp
            }
        };

        _ = _socket.Send(offerMessage);
        //Debug.Log("Offer sent");
    }

    private void RequestCoroutine(IEnumerator coroutine)
    {
        CoroutineRequested?.Invoke(coroutine);
    }
}
