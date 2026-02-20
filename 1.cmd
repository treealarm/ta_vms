gst-launch-1.0 -e -v ^
  rtspsrc location="rtsp://orwell:elvees@172.16.254.105:554/cam/realmonitor?channel=1&subtype=0&unicast=true&proto=Onvif" ^
          protocols=tcp latency=500 name=src ^
  src. ! application/x-rtp,media=video,encoding-name=H264 ! rtph264depay ! h264parse config-interval=1 ! queue ^
        ! splitmuxsink muxer=mp4mux location="D:/TESTS/vms_rec/record_%05d.mp4" max-size-time=10000000000
