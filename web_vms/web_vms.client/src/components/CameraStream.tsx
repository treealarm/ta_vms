import { Box } from '@mui/material';
import React, { useEffect, useRef } from 'react';

interface CameraStreamProps {
  streamName: string;
  baseUrl?: string;
}

const CameraStream: React.FC<CameraStreamProps> = ({ streamName, baseUrl }) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const stoppedRef = useRef(false);


  useEffect(() => {
    if (!streamName) return;

    stoppedRef.current = false;

    const start = async () => {
      while (!stoppedRef.current) {
        try {
          const pc = new RTCPeerConnection({
            iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
          });

          pcRef.current = pc;

          pc.ontrack = (event) => {
            if (videoRef.current) {
              videoRef.current.srcObject = event.streams[0];
            }
          };

          pc.onconnectionstatechange = () => {
            const state = pc.connectionState;

            if (state === 'failed' || state === 'disconnected' || state === 'closed') {
              console.warn('WebRTC disconnected, retrying...');
              pc.close();
            }
          };

          const offer = await pc.createOffer({
            offerToReceiveAudio: true,
            offerToReceiveVideo: true,
          });

          await pc.setLocalDescription(offer);

          const url = `${(baseUrl || 'http://localhost:8889').replace(/\/$/, '')}/${encodeURIComponent(streamName)}/whep`;

          const resp = await fetch(url, {
            method: 'POST',
            headers: {
              'Content-Type': 'application/sdp',
            },
            body: offer.sdp,
          });

          if (!resp.ok) {
            throw new Error(`WHEP failed: ${resp.status}`);
          }

          const answerSDP = await resp.text();

          await pc.setRemoteDescription({
            type: 'answer',
            sdp: answerSDP,
          });

          // Ждём пока соединение не развалится
          await new Promise<void>((resolve) => {
            pc.onconnectionstatechange = () => {
              if (
                pc.connectionState === 'failed' ||
                pc.connectionState === 'disconnected' ||
                pc.connectionState === 'closed'
              ) {
                resolve();
              }
            };
          });

        } catch (err) {
          console.warn('WebRTC retry error:', err);
        }

        // задержка перед реконнектом
        await new Promise((r) => setTimeout(r, 2000));
      }
    };

    start();

    return () => {
      stoppedRef.current = true;
      pcRef.current?.close();
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
        borderRadius: 2
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