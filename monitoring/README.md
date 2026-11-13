# Zabbix 7.4 monitoring for V380Decoder

This integration monitors the **camera's decoded media reception**, not only the Docker container or listening TCP port. It works with this fork of V380Decoder, independently of the original camera's SNMP capabilities.

## Decoder requirements

Start one decoder instance per camera with `--output rtsp --enable-api` and a reachable HTTP port. For an authenticated API, use `--secure` (a flag, not a required `true` value). Existing credentials configured with `--username` and `--password` protect all endpoints, including `/api/metrics`.

Example for a single local decoder (substitute your camera settings):

```sh
./V380Decoder --id 12345678 --ip 192.168.20.50 --port 8800 \
  --username admin --password 'CAMERA_PASSWORD' \
  --output rtsp --enable-api --enable-onvif --secure \
  --http-port 8080 --rtsp-port 8554 --camera-name "Entrance"
```

For Docker, publish/map the API's actual HTTP port, and use the decoder's reachable server address, **not** the camera IP, as the Zabbix host interface. In a multi-camera deployment, assign each decoder instance a distinct HTTP port (and RTSP port, where relevant).

Test from the Zabbix server or proxy:

```sh
curl --fail --silent --show-error -u 'admin:CAMERA_PASSWORD' \
  http://DECODER_HOST:8080/api/metrics
```

When `--secure` is not used, authentication is not required. Do not expose an unauthenticated API to untrusted networks. Basic authentication over ordinary HTTP is not encrypted; use HTTPS via a reverse proxy if traffic crosses untrusted networks, and restrict the HTTP port to monitoring/management systems.

## Install the Zabbix template

1. In Zabbix 7.4, go to **Data collection → Templates → Import**, and select [zabbix_7.4_v380decoder.yaml](zabbix_7.4_v380decoder.yaml).
2. Create one Zabbix host per decoder instance and link **V380 Decoder by HTTP**. **Do not add a host interface**: the HTTP agent resolves the decoder address from `{$V380.HOST}`. No Zabbix agent or SNMP is required.
3. Set `{$V380.HOST}` to the decoder server's reachable IP address or DNS name (for example `192.168.10.11`), and `{$V380.HTTP.PORT}` to the mapped API port (default `8080`). Set `{$V380.USER}` to the configured username and override `{$V380.PASSWORD}` on the **host level as Secret text**. The host macro can be shared at template level if all decoders run on one server. Do not save real credentials in the template.
4. If using an HTTPS reverse proxy, set `{$V380.SCHEME}` to `https`; adjust port as needed. The default polling interval macro is `{$V380.UPDATE.INTERVAL}=30s`.
5. Check **Monitoring → Latest data**. The master item `v380.metrics.raw` performs one HTTP request and all 75 other items are dependent on its JSON response.

The HTTP Agent item uses HTTP Basic authentication. This fork also accepts HTTP Digest, but Basic is sufficient when `--secure` is enabled and HTTPS is used for untrusted transport.

## Measurements

One `GET /api/metrics` JSON object includes the following classes of values:

- **Identity:** camera ID and configured name; LAN/cloud mode; upstream address/port; discovered MAC, IP, subnet, gateway (when UDP discovery succeeds); protocol/device type and vendor identifiers from the authentication response.
- **Availability:** upstream connection state; whether streaming has started; fresh video and audio flags; elapsed time since the last packet and decoded frame; last authentication, stream start and error timestamps; process and current session uptime; authentication/login duration.
- **Video:** negotiated H.264/H.265 codec, resolution, header-reported FPS, measured 10-second FPS and decoded-payload bitrate, total decoded frames/bytes, keyframes and malformed video payloads, decryption mode/header size/validation score.
- **Audio:** detected output format (G.711 PCMA or IMA-ADPCM decoded to PCMA), age since last decoded audio, 10-second audio frame/bitrate measurements, output frames/bytes and skipped audio frames.
- **Transport:** upstream packets/bytes (the assembled TCP-protocol message payload plus 12-byte fragment header), rolling 10-second upstream bitrate and packet rate, invalid fragment headers, unknown protocol packets and receive errors.
- **RTSP:** all connected RTSP sessions, sessions actively playing, H.264 compatibility sessions, video/audio subscribers and clients using UDP.
- **Recovery:** authentication attempts/failures, stream login failures, connection/stream starts, reconnect attempts and categorized last error.

Health flags are numeric: `stream_connected=1` means that the upstream stream has been started and its session has not yet ended; `video_healthy=1` and `audio_healthy=1` additionally require a respective decoded frame within the previous **15 seconds**. If a video/audio frame has never been observed, the corresponding `*_frame_age_seconds` is **-1**. Video health is independent of RTSP viewer count.

The measured 10-second rates divide traffic in the last ten whole-second buckets by 10, including zero-traffic intervals. Bitrate describes decoded/received application payload, not physical link throughput, HTTP or RTP overhead. The video FPS from a frame header is nominal, not independently measured. Counters reset when the decoder process restarts.

## Alerts

Six template triggers detect missing HTTP metrics, an upstream connection down for 2 minutes, absent video for 2 minutes, a new authentication failure, at least 3 reconnect attempts within 5 minutes, and new video decode failures. Adjust trigger intervals to your environment. Audio is monitored but does **not** trigger an alert by default, since some cameras do not provide supported audio.

## Limits

V380's current reverse-engineered protocol **does not expose reliable battery level, Wi-Fi signal, SD card wear/free space, camera-side CPU/memory load, precise firmware build, IR LED state, or motion-event count** through this decoder. These are deliberately not fabricated. `device_version` is a protocol/device version field from the camera's login reply, not a firmware version string. LAN discovery metadata can be blank in Docker/cloud mode.

The original `GET /api/status` behavior remains unchanged for compatibility. Use `GET /api/metrics` for Zabbix; a running HTTP server by itself is not proof of live video.

## Test

```sh
dotnet test tests/V380Decoder.Tests/V380Decoder.Tests.csproj --configuration Release
```

Tests include telemetry counter updates and security of the metrics endpoint. A successful run against the actual deployed camera and Zabbix import should still be verified on your server.
