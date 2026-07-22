# Preview 24 monitoring performance groundwork

## Goal

Keep every visible thumbnail moving for 100 clients without turning the manager
into 100 full remote-control sessions. Monitoring and control use separate
quality profiles, queues, and lifecycles.

## Measured reference

The LinkIO reference was observed on an Intel Core i5-13600KF, 48 GB RAM, and an
NVIDIA RTX 3060 Ti while showing roughly 137 PCs. The process used about 3.7%
CPU, 423 MB working set, 741 MB private memory, 5.5% GPU, and the active adapter
received about 9 Mbps. The UI appeared to use a monitoring frame setting near
15 while the control-window profile was configured separately.

These figures are an observational baseline, not a claim about LinkIO's wire
format. They imply that monitoring thumbnails are low-resolution encoded video
with aggressive frame replacement, not independent full-resolution desktop
streams.

## Preview 24 first bundle

- `MonitoringProtocol` is a fixed, versioned binary envelope for video frames,
  status, stream settings, subscriptions, and keyframe requests.
- Packet sizes, identifiers, enum values, video metadata, and UTF-8 input are
  bounded and validated before allocation or decoding.
- `ThumbnailLoadSimulator` models one pooled latest-frame slot per client. A
  slow manager drops stale frames instead of accumulating latency and memory.
- Automatic monitoring profiles start at 320x180 for small groups and scale to
  160x90 at 100 clients. Groups above 100 default to 10 FPS and 60 Kbps until
  the manager confirms spare capacity.

## Acceptance targets

For the synthetic 100-client baseline:

- 160x90, 15 FPS, approximately 80 Kbps per client
- no malformed packets accepted and zero protocol errors
- manager-side queue latency p95 below 50 ms
- aggregate monitoring traffic around 8-12 Mbps
- bounded memory even if decode or rendering temporarily falls behind

The simulator intentionally excludes screen capture, real H.264 encode/decode,
relay overhead, and GPU composition. Those measurements must be added before
the feature is called production-ready.

## Implementation sequence

1. Replace the existing thumbnail JPEG polling path with persistent monitoring
   subscriptions and the shared packet envelope.
2. Add client-side hardware H.264 encoding with software fallback and one
   encoder shared by all monitoring subscribers.
3. Add a manager decode pool and a single GPU-composed thumbnail surface. Only
   the newest frame for each tile may enter the render pass.
4. Keep the existing control session at its own resolution, FPS, and bitrate;
   opening it must not restart the monitoring stream.
5. Add automatic quality control using decode time, packet loss, queue age, and
   manager CPU/GPU load, with per-group manual overrides.
6. Run real 25/50/100/137-client soak tests and record CPU, GPU, memory,
   throughput, frame cadence, and reconnect behavior before release.
