import { Box } from '@mui/material';
import React, { useEffect, useRef } from 'react';

interface CameraStreamProps {
  streamName: string;
  baseUrl?: string;
}

const CameraStream: React.FC<CameraStreamProps> = ({ streamName, baseUrl }) => {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    if (!streamName) return;

    const pc = new RTCPeerConnection({
      iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
    });

    pc.ontrack = (event) => {
      if (videoRef.current) {
        const stream = event.streams[0];
        videoRef.current.srcObject = stream;
      }
    };

    const start = async () => {
      try {
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
      } catch (err) {
        console.error('WebRTC error:', err);
      }
    };

    start();

    return () => {
      pc.close();
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
    >    <video
        ref={videoRef}
        autoPlay
        playsInline
        muted   // <-- ключевой момент
        controls
        style={{ width: '100%', height: '100%' }}
      />
    </Box>

  );
};

export default CameraStream;