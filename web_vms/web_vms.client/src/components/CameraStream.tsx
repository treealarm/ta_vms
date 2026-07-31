import { Box } from '@mui/material';
import React, { useEffect, useRef } from 'react';
import BboxOverlay from './BboxOverlay';
import { iceServersFromStunUrl } from '../utils/iceServers';

interface CameraStreamProps {
  whepUrl: string;
  /** Camera to pull analytics bboxes for; only used when bboxEnabled. */
  cameraId?: string;
  /** When true, overlays live analytics bboxes on the video (opt-in per card). */
  bboxEnabled?: boolean;
  /** STUN URL from the camera's media router (effectiveWebrtcStunUrl): undefined = public
   *  default, empty string = no STUN (LAN only). */
  stunUrl?: string | null;
}

const CameraStream: React.FC<CameraStreamProps> = ({ whepUrl, cameraId, bboxEnabled = false, stunUrl }) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const retryTimeoutRef = useRef<number | null>(null);
  const sessionUrlRef = useRef<string | null>(null);

  useEffect(() => {
    if (!whepUrl) {
      console.error('whepUrl is null');
      return;
    }

    let stopped = false;
    let sessionId = 0;
    let reconnecting = false;

    const closeSession = () => {
      if (!sessionUrlRef.current) {
        return;
      }
      const url = sessionUrlRef.current;
      sessionUrlRef.current = null;
      fetch(url, { method: 'DELETE' }).catch(() => {
        // best-effort: MediaMTX will time out the session on its own otherwise
      });
    };

    const cleanupAndRetry = (pc?: RTCPeerConnection) => {
      if (stopped || reconnecting) {
        return;
      }

      reconnecting = true;

      console.warn('WebRTC disconnected, retrying...');

      closeSession();

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
        // A WHEP session may have just been opened (sessionUrlRef set right before this check)
        // for a connection attempt that's now stale — close it instead of leaking it on MediaMTX.
        closeSession();
        return true;
      }

      return false;
    };

    // This is a non-trickle WHEP exchange (single offer/answer, no PATCH for late-arriving
    // candidates), so any candidate gathered after the offer is sent is lost. Wait for gathering
    // to finish first — capped, so an unreachable STUN server can't stall the connection, since
    // host candidates (enough for same-LAN viewing) are normally ready almost immediately.
    const waitForIceGathering = (pc: RTCPeerConnection, timeoutMs: number) =>
      new Promise<void>((resolve) => {
        if (pc.iceGatheringState === 'complete') {
          resolve();
          return;
        }

        const onChange = () => {
          if (pc.iceGatheringState === 'complete') {
            clearTimeout(timer);
            pc.removeEventListener('icegatheringstatechange', onChange);
            resolve();
          }
        };

        const timer = window.setTimeout(() => {
          pc.removeEventListener('icegatheringstatechange', onChange);
          resolve();
        }, timeoutMs);

        pc.addEventListener('icegatheringstatechange', onChange);
      });

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
        iceServers: iceServersFromStunUrl(stunUrl),
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

        await waitForIceGathering(pc, 1500);

        if (isSessionExpired(currentSession, pc)) {
          return;
        }

        const resp = await fetch(whepUrl, {
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

        // Keep this relative (don't resolve to an absolute URL): installAuthFetch only attaches
        // the Bearer token to requests whose URL starts with '/api/', and fetch() already
        // resolves a relative Location against the current origin on its own.
        sessionUrlRef.current = resp.headers.get('Location');

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

      closeSession();
    };
  }, [whepUrl, stunUrl]);

  return (
    <Box
      className="vms-video-frame"
      sx={{
        width: '100%',
        height: '100%',
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
      {/* Analytics bbox overlay — a separate SSE channel + <canvas> layer, fully decoupled from the
          WebRTC pipeline above (and it owns the detection hook, so detection updates never re-render
          this component or the <video>), so enabling it never adds latency to the live video. */}
      {bboxEnabled && cameraId && <BboxOverlay videoRef={videoRef} cameraId={cameraId} />}
    </Box>
  );
};

export default CameraStream;