import { Box } from '@mui/material';
import React, { useEffect, useRef } from 'react';

interface CameraStreamProps {
  streamName: string;
  baseUrl?: string;
}

const CameraStream: React.FC<CameraStreamProps> = ({ streamName, baseUrl }) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const retryTimeoutRef = useRef<number | null>(null);

  useEffect(() => {
    if (!streamName) {
      console.error('stream is null');
      return;
    }

    let stopped = false;
    let sessionId = 0;
    let reconnecting = false;

    const cleanupAndRetry = (pc?: RTCPeerConnection) => {
      if (stopped || reconnecting) {
        return;
      }

      reconnecting = true;

      console.warn('WebRTC disconnected, retrying...');

      if (pc) {
        pc.onconnectionstatechange = null;
        pc.ontrack = null;
        pc.close();
      }

      if (pcRef.current) {
        pcRef.current.onconnectionstatechange = null;
        pcRef.current.ontrack = null;
        pcRef.current.close();
        pcRef.current = null;
      }

      if (retryTimeoutRef.current) {
        clearTimeout(retryTimeoutRef.current);
      }

      retryTimeoutRef.current = window.setTimeout(() => {
        if (stopped) {
          return;
        }

        reconnecting = false;
        startConnection();
      }, 2000);
    };

    const isSessionExpired = (
      currentSession: number,
      pc: RTCPeerConnection
    ) => {
      if (stopped || currentSession !== sessionId) {
        pc.onconnectionstatechange = null;
        pc.ontrack = null;
        pc.close();
        return true;
      }

      return false;
    };

    const startConnection = async () => {
      if (stopped) {
        return;
      }

      const currentSession = ++sessionId;

      if (pcRef.current) {
        pcRef.current.onconnectionstatechange = null;
        pcRef.current.ontrack = null;
        pcRef.current.close();
        pcRef.current = null;
      }

      const pc = new RTCPeerConnection({
        iceServers: [
          {
            urls: 'stun:stun.l.google.com:19302',
          },
        ],
      });

      pcRef.current = pc;

      pc.ontrack = (event) => {
        if (videoRef.current && event.streams?.[0]) {
          videoRef.current.srcObject = event.streams[0];
        }
      };

      pc.onconnectionstatechange = () => {
        const state = pc.connectionState;

        console.log('PC state:', state);

        if (state === 'failed' || state === 'disconnected') {
          cleanupAndRetry(pc);
        }
      };

      try {
        const offer = await pc.createOffer({
          offerToReceiveAudio: true,
          offerToReceiveVideo: true,
        });

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        await pc.setLocalDescription(offer);

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        const url = `${(
          baseUrl ||
          `${window.location.protocol}//${window.location.hostname}:8889`
        ).replace(/\/$/, '')}/${streamName}/whep`;

        console.log('WHEP url =', url);

        const resp = await fetch(url, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/sdp',
          },
          body: offer.sdp,
        });

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        if (!resp.ok) {
          throw new Error(`WHEP failed: ${resp.status}`);
        }

        const answerSDP = await resp.text();

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        await pc.setRemoteDescription({
          type: 'answer',
          sdp: answerSDP,
        });

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        reconnecting = false;
        console.log('WebRTC connected successfully');
      } catch (err) {
        console.error('WebRTC connection error:', err);
        cleanupAndRetry(pc);
      }
    };

    startConnection();

    return () => {
      stopped = true;

      if (retryTimeoutRef.current) {
        clearTimeout(retryTimeoutRef.current);
        retryTimeoutRef.current = null;
      }

      if (pcRef.current) {
        pcRef.current.onconnectionstatechange = null;
        pcRef.current.ontrack = null;
        pcRef.current.close();
        pcRef.current = null;
      }
    };
  }, [streamName, baseUrl]);

  return (
    <Box
      title={streamName}
      className="vms-video-frame"
      sx={{
        width: '100%',
        aspectRatio: '1 / 1',
        overflow: 'hidden',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
      }}
    >
      <video
        ref={videoRef}
        autoPlay
        playsInline
        muted
        controls
        style={{
          width: '100%',
          height: '100%',
          objectFit: 'contain',
        }}
      />
    </Box>
  );
};

export default CameraStream;