import { Box } from '@mui/material';
import React, { useEffect, useRef } from 'react';
import BboxOverlay from './BboxOverlay'
import EventOverlay from './EventOverlay';
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

// A WHEP exchange can succeed in full — session created, peer connection reporting `connected` —
// and still deliver no media. Nothing in the RTCPeerConnection API reports that, so the only proof
// that frames are arriving is the element's own clock. These bound how long a silent cell is left
// alone before it is torn down and re-negotiated.
// Bounds how long one offer may occupy a connection. A browser allows six per origin, and a WHEP
// offer for a path MediaMTX has not brought up yet used to sit there for twelve seconds — long
// enough, with two of them, to starve everything else on the page including the token refresh,
// which then failed and took the whole session down. The gateway now gives up on MediaMTX after
// four seconds and answers 503 (see WhepProxyService.MediaMtxOfferTimeout); this is the backstop
// for the gateway itself going quiet, with room for the push it kicks off first.
const WHEP_OFFER_TIMEOUT_MS = 8000;
// Deliberately a fixed, short interval rather than a growing one. The usual reason an offer fails
// is that the stream is still being brought up on demand — the gateway says 503 for exactly that —
// and it becomes available within seconds, so this is the moment to keep asking. Backing off here
// was tried and measured: a cell toggled between live and archive never got a retry inside its own
// live window again, and stayed black.
const RETRY_MS = 2000;
const PROGRESS_CHECK_MS = 3000;
const PROGRESS_STALL_LIMIT = 5;

const CameraStream: React.FC<CameraStreamProps> = ({ whepUrl, cameraId, bboxEnabled = false, stunUrl }) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const retryTimeoutRef = useRef<number | null>(null);
  const progressTimerRef = useRef<number | null>(null);

  useEffect(() => {
    if (!whepUrl) {
      console.error('whepUrl is null');
      return;
    }

    let stopped = false;
    let sessionId = 0;
    let reconnecting = false;
    // Scoped to this effect run, not a ref: when the effect re-runs (whepUrl/stunUrl change), the
    // old run's cleanup must only ever DELETE the session it opened itself, never the new one's.
    let sessionUrl: string | null = null;

    const closeSession = () => {
      if (!sessionUrl) {
        return;
      }
      const url = sessionUrl;
      sessionUrl = null;
      fetch(url, { method: 'DELETE' }).catch(() => {
        // best-effort: MediaMTX will time out the session on its own otherwise
      });
    };

    const stopWatchdog = () => {
      if (progressTimerRef.current !== null) {
        clearInterval(progressTimerRef.current);
        progressTimerRef.current = null;
      }
    };

    const cleanupAndRetry = (pc?: RTCPeerConnection) => {
      if (stopped || reconnecting) {
        return;
      }

      reconnecting = true;

      console.warn('WebRTC disconnected, retrying...');

      stopWatchdog();
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
      }, RETRY_MS);
    };

    // Watches the one thing that proves media is arriving. Covers both a connection that never
    // completes and one that reports `connected` while MediaMTX streams to nobody — the state a
    // torn-down and re-created cell leaves behind, where the old session is still being fed and
    // the visible element has nothing attached to it.
    const startWatchdog = (pc: RTCPeerConnection) => {
      stopWatchdog();
      let lastTime = -1;
      let stalledChecks = 0;

      progressTimerRef.current = window.setInterval(() => {
        if (stopped || reconnecting) {
          return;
        }

        const now = videoRef.current?.currentTime ?? 0;
        if (now > lastTime) {
          lastTime = now;
          stalledChecks = 0;
          return;
        }

        stalledChecks += 1;
        if (stalledChecks >= PROGRESS_STALL_LIMIT) {
          console.warn('WebRTC delivered no frames, re-negotiating');
          cleanupAndRetry(pc);
        }
      }, PROGRESS_CHECK_MS);
    };

    const isSessionExpired = (
      currentSession: number,
      pc: RTCPeerConnection
    ) => {
      if (stopped || currentSession !== sessionId) {
        stopWatchdog();
        pc.onconnectionstatechange = null;
        pc.ontrack = null;
        pc.close();
        // A WHEP session may have just been opened (sessionUrl set right before this check)
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

      let pc: RTCPeerConnection;
      try {
        pc = new RTCPeerConnection({ iceServers: iceServersFromStunUrl(stunUrl) });
      } catch (err) {
        // A malformed STUN URL in the media router config makes every attempt throw the same way,
        // so retrying would just spin — report it and leave playback stopped instead.
        console.error('Invalid ICE configuration, live playback disabled:', stunUrl, err);
        return;
      }

      pcRef.current = pc;

      pc.ontrack = (event) => {
        if (videoRef.current && event.streams?.[0]) {
          videoRef.current.srcObject = event.streams[0];
        }
      };

      pc.onconnectionstatechange = () => {
        const state = pc.connectionState;

        console.log('PC state:', state);

        // 'closed' belongs here too: every close this component performs itself nulls this handler
        // first, so reaching it means the connection went away on its own.
        if (state === 'failed' || state === 'disconnected' || state === 'closed') {
          cleanupAndRetry(pc);
          return;
        }

        // Armed here rather than when signalling finished: MediaMTX brings a path up on demand, so
        // the wait between a completed offer/answer and the first frame legitimately covers the
        // media server starting to publish. Watching that window would tear an attempt down exactly
        // as it was about to succeed.
        if (state === 'connected') {
          startWatchdog(pc);
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

        const offerAbort = new AbortController();
        const offerDeadline = window.setTimeout(() => offerAbort.abort(), WHEP_OFFER_TIMEOUT_MS);
        let resp: Response;
        try {
          resp = await fetch(whepUrl, {
            method: 'POST',
            headers: {
              'Content-Type': 'application/sdp',
            },
            body: offer.sdp,
            signal: offerAbort.signal,
          });
        } finally {
          clearTimeout(offerDeadline);
        }

        // Recorded BEFORE the staleness check below, and that order is the whole point: the check
        // tears the attempt down and calls closeSession(), so anything learned after it can no
        // longer be closed. Reading it later left every negotiation that was in flight when the
        // cell was torn down — switching a tile between live and archive does exactly that — as a
        // session MediaMTX keeps feeding forever, with nothing on the page attached to it.
        //
        // Keep this relative (don't resolve to an absolute URL): installAuthFetch only attaches
        // the Bearer token to requests whose URL starts with '/api/', and fetch() already
        // resolves a relative Location against the current origin on its own.
        sessionUrl = resp.headers.get('Location');

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
      } catch (err) {
        console.error('WebRTC connection error:', err);
        cleanupAndRetry(pc);
      }
    };

    startConnection();

    return () => {
      stopped = true;
      stopWatchdog();

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
      {/* Same switch, the other half of the picture: cameras whose analytics produces no geometry
          (cell motion, tamper) announce a condition instead, and it shows as text where a box
          would be. */}
      {bboxEnabled && cameraId && <EventOverlay cameraId={cameraId} enabled />}
    </Box>
  );
};

export default CameraStream;