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
      console.error("stream is null")
      return;
    }
      

    let stopped = false;

    const startConnection = async () => {
      if (stopped) return;

      const pc = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
      });
      pcRef.current = pc;

      pc.ontrack = (event) => {
        if (videoRef.current) {
          videoRef.current.srcObject = event.streams[0];
        }
      };

      const cleanupAndRetry = () => {
        if (stopped) return;

        console.warn('WebRTC disconnected, retrying...');
        pc.close();
        pcRef.current = null;

        // небольшая задержка перед повторным соединением
        retryTimeoutRef.current = window.setTimeout(() => {
          startConnection();
        }, 2000);
      };

      pc.onconnectionstatechange = () => {
        const state = pc.connectionState;
        if (state === 'failed' || state === 'disconnected' || state === 'closed') {
          cleanupAndRetry();
        }
      };

      try {
        const offer = await pc.createOffer({
          offerToReceiveAudio: true,
          offerToReceiveVideo: true,
        });
        await pc.setLocalDescription(offer);

        const url = `${(baseUrl || 'http://localhost:8889').replace(/\/$/, '')}/${encodeURIComponent(streamName)}/whep`;
        console.log("url=", url);
        const resp = await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/sdp' },
          body: offer.sdp,
        });

        if (!resp.ok) throw new Error(`WHEP failed: ${resp.status}`);

        const answerSDP = await resp.text();
        await pc.setRemoteDescription({ type: 'answer', sdp: answerSDP });
      } catch (err) {
        console.warn('WebRTC connection error, retrying...', err);
        cleanupAndRetry();
      }
    };

    startConnection();

    return () => {
      stopped = true;
      if (retryTimeoutRef.current) {
        clearTimeout(retryTimeoutRef.current);
        retryTimeoutRef.current = null;
      }
      pcRef.current?.close();
      pcRef.current = null;
    };
  }, [streamName, baseUrl]);

  return (
    <Box
      title={streamName}
      sx={{
        width: '100%',
        aspectRatio: '1 / 1',
        border: 1,
        borderColor: 'divider',
        borderRadius: 2,
        overflow: 'hidden',
      }}
    >
      <video
        ref={videoRef}
        autoPlay
        playsInline
        muted
        controls
        style={{ width: '100%', height: '100%' }}
      />
    </Box>
  );
};

export default CameraStream;