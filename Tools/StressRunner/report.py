import json, sys, statistics, shutil
from pathlib import Path

root = Path(__file__).resolve().parents[2]
base = root / 'artifacts/fps-stress'
runs = {style: json.loads((base / folder / 'analysis.json').read_text()) for style,folder in [('Default','default-final'),('Liquid Glass','liquidglass-final')]}
labels = {'baseline-active':'Media ổn định (paused)', 'transition-50Hz':'Spam chuyển view', 'media-1000Hz':'Flood media', 'mixed-burst':'Media + view + đổi theme', 'cpu-contention':'Tranh chấp CPU/RAM + cập nhật', 'recovery-active':'Hồi phục, mở media', 'recovery-collapsed':'Hồi phục, thu gọn', 'steady-playing':'Media playing mô phỏng'}
text = '''# FPS và stress V-Notch — 23/09/2026

**Kết quả: hai lượt cuối không tự crash, nhưng chưa chịu được tải CPU/RAM cực nặng mà không cần cắt nguồn tải.** Callback FPS giảm xuống 10,1 (Default) và 7,0 (Liquid Glass); probe dispatcher ưu tiên Background chờ tối đa 22,88 và 32,43 giây. Watchdog ngừng bơm tải, hàng đợi sau đó về 0 và app tiếp tục chạy. Đây là phục hồi sau quá tải, không phải vượt qua stress không giới hạn.

Đo trên cửa sổ MainWindow thật và service graph của bản Release, được host trong process StressRunner riêng. Đây là stress bằng API nội bộ/debug và dữ liệu media mô phỏng, không phải script bấm chuột và không phải bài kiểm thử Spotify/network đầu-cuối.

## FPS và thời gian khung hình

**FPS trong bảng là tần số callback WPF CompositionTarget.Rendering khác RenderingTime**, đo bằng Stopwatch. Nó không chứng minh số frame thực sự hiển thị trên màn hình. Đã thử PresentMon 2.6.0 theo PID và có ETW session hoạt động nhưng không nhận được CSV/frame của process này. Không suy diễn FPS màn hình từ dữ liệu trống. Cửa sổ WPF của app bật AllowsTransparency; chưa xác minh nguyên nhân PresentMon không bắt được frame. [Tài liệu PresentMon](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md).

Màn hình báo 2560×1440, 144 Hz; Ryzen 7 5800H, 16 logical CPUs; Radeon integrated + RTX 3050 Laptop; WPF tier 2, .NET 8.0.29, Windows 11 10.0.26200. Bản Debug của người dùng còn chạy ở các lượt thăm dò; khi bắt đầu lượt Liquid Glass cuối thì không còn process V-Notch.exe khác. Tải desktop không được cô lập hoàn toàn, và hai nhóm lượt đo diễn ra ở thời điểm khác nhau. Đây không phải phép so A/B trước/sau tối ưu.

| Cấu hình | Pha | Thời gian s | Callback FPS | p95 ms | p99 ms | Max ms | Probe UI max ms |
|---|---|---:|---:|---:|---:|---:|---:|
'''
for style,run in runs.items():
 for p in run['phases']:
  text += f"| {style} | {labels[p['name']]} | {p['seconds']:.1f} | {p['callbackFps']:.1f} | {p['frameP95']:.1f} | {p['frameP99']:.1f} | {p['frameMax']:.1f} | {p['dispatcherMax']:.1f} |\n"
text += '''
Frame time ở đây là khoảng cách giữa callback, không phải GPU render duration. Probe UI được post ở DispatcherPriority.Background, vì vậy độ trễ probe chứng minh starvation ở ưu tiên đó, không đồng nghĩa UI hoàn toàn treo hoặc đo trực tiếp input latency. Subscription đo FPS có thể giữ render pump hoạt động; pha collapsed không được dùng để kết luận CPU/pin idle bình thường.

## Tải đã thực hiện và tài nguyên

| Cấu hình | Hoàn tất | Media callback xử lý | Yêu cầu chuyển view đã gọi | Queue harness tối đa | Harness bỏ yêu cầu | Peak CPU app % | Peak private MiB | Peak working set MiB |
|---|---|---:|---:|---:|---:|---:|---:|---:|
'''
for style,run in runs.items():
 phases=run['phases']
 text += f"| {style} | {run['completedFile']} | {sum(p['mediaProcessed'] for p in phases):,} | {sum(p['transitionCalls'] for p in phases):,} | {run['peakHarnessQueue']} | {sum(p['droppedByHarness'] for p in phases)} | {max(p['cpuPercent']['peak'] for p in phases):.1f} | {max(p['privateMiB']['peak'] for p in phases):.1f} | {max(p['workingMiB']['peak'] for p in phases):.1f} |\n"
text += '''
Workload bên ngoài vừa tạo tải CPU vừa tranh chấp băng thông bộ nhớ. CPU được chuẩn hóa trên 16 logical CPUs và không tính process tạo tải CPU bên ngoài. Process tạo tải có tối đa 8 worker, khoảng 128 MiB buffer, chạy khoảng 32 giây. Bơm media nhắm 1000 event/s, đổi track/artwork mỗi 20 event với 24 ảnh frozen 256×256. Chuyển view nhắm 50 request/s; pha mixed thêm đổi Default/Liquid Glass nhắm 5 lần/s. Tần suất thực tế phụ thuộc scheduler và được tính từ số callback/thời gian, không mặc định bằng target.

Ở Default, pha CPU bị rút ngắn bởi watchdog nên process tranh chấp CPU còn chạy khoảng 9 giây đầu pha hồi phục; số liệu hồi phục bao gồm khoảng chồng lấn đó.

Media callback được gọi không đồng nghĩa có từng ấy frame hiển thị: OnMediaChanged lại post lên dispatcher và bỏ snapshot cũ khi đã có snapshot mới. Số chuyển view là lời gọi API, không phải số animation hoàn tất; việc supersede/cancel là bình thường dưới spam.

| Cấu hình | Private MiB đầu baseline → cuối hồi phục mở | Handles đầu baseline → cuối hồi phục mở | Invalid transition warnings | Transition requested / completed trong log | ERROR log / render failed |
|---|---|---|---:|---|---|
'''
for style,run in runs.items():
 p=run['phases'][0]; r=next(p for p in run['phases'] if p['name']=='recovery-active'); w=run['warnings']
 text += f"| {style} | {p['privateMiB']['first']:.1f} → {r['privateMiB']['last']:.1f} | {p['handles']['first']} → {r['handles']['last']} | {w['invalidStateTransitions']:,} | {w['transitionRequested']:,} / {w['transitionCompleted']:,} | {w['errors']} / {w['renderFailed']} |\n"
text += '''
Không forced-GC trong pha hồi phục. Tăng retained memory sau warmup có thể do cache/GPU resources; các lượt vài phút không đủ khẳng định hoặc loại trừ leak dài hạn.

Hoàn tất runner không đồng nghĩa vượt qua toàn bộ tải không giới hạn. Những pha dưới đây đã bị watchdog ngừng bơm thêm tải, rồi chờ hàng đợi drain:

'''
for style,run in runs.items():
 stopped=[p for p in run['phases'] if p.get('loadStoppedByWatchdog')]
 text += f"- {style}: " + (', '.join(f"{p['name']} ({p['seconds']:.1f} s gồm drain, probe max {p['dispatcherMax']/1000:.2f} s)" for p in stopped) if stopped else 'không cắt tải') + '.\n'
text += '''

## Phát hiện và giới hạn

- App tiếp nhận flood media tốt hơn tải chuyển view/theme kết hợp. Cần nhìn p95/p99 và hàng đợi, không chỉ FPS trung bình hay việc process còn sống.
- Có hàng nghìn cảnh báo `Invalid transition`, chủ yếu `Collapsing → Expanding`, và cả `Expanded → Expanded`. Đây là dấu hiệu nên rà soát tính nhất quán giữa NotchStateManager và NotchTransitionCoordinator khi bị spam; chưa chứng minh lỗi ở tốc độ tương tác người dùng bình thường. Đường debug nội bộ có thể bỏ qua gating của UI.
- Lượt thăm dò Default (`default-v3`) bị watchdog cũ kết thúc khi Background probe chờ 15,56 giây, queue harness 601, private khoảng 306,5 MiB. Đây là termination do công cụ, không phải app tự crash.
- Lượt thăm dò Glass (`liquidglass-v1`) đạt khoảng 19,2 callback FPS ở mixed, p95 115 ms, probe max 7,23 s. Khi tranh chấp CPU, probe chờ 30,40 s nhưng render callback gần nhất vẫn chỉ cách 119 ms; watchdog cũ kết thúc process. Vì thế không gọi đây là full UI deadlock. Hai lượt cuối dùng cùng watchdog mới: dừng nguồn tải sau 15 s probe starvation, cho queue cơ hội drain, chỉ hard-stop khi cả render/probe ngừng lâu, RAM >2 GiB hoặc tổng thời gian >300 s.
- Các lỗi ResourceAssembly/khởi tạo XAML ở ba lần dựng harness ban đầu được loại khỏi đánh giá lỗi app. Source production không được thay đổi để che lỗi trong lúc stress.
- Profile settings riêng, startup manager no-op, không network lookup/Spotify Canvas/lyrics/weather/smart crop; không seek/play media thật, không thay volume, không bật camera. Media detection thật dừng sau warmup để dữ liệu không trộn vào workload. Service privacy/audio enumeration và glass capture vẫn là implementation production.
- Baseline và recovery-active dùng dữ liệu có IsAnyMediaPlaying=true, IsPlaying=false; final steady-playing dùng IsPlaying=true. Không có audio phát thật. Không suy rộng sang webcam 1080p, Spotify OAuth/HTTP, ONNX, mất thiết bị, suspend/resume, crash driver, pin hay soak nhiều giờ.

## Tái lập và dữ liệu

Xem `Tools/StressRunner/README.md` để build/chạy lại. Công cụ stress có giới hạn queue, thời gian, RAM và CPU worker. Không chạy xUnit hay test suite không liên quan.

- Hai lượt chính: `artifacts/fps-stress/default-final/` và `artifacts/fps-stress/liquidglass-final/`.
- Mỗi thư mục có `summary.json`, `analysis.json`, `telemetry.jsonl`, `phases.jsonl`, `runtime.log`, stdout/stderr và marker hoàn tất/thất bại.
- Bản tóm tắt JSON để lưu cùng source: `Tools/StressRunner/Measurements/`.
- Bộ đo dùng production DLL SHA256 `432D81DEF664A0D721102F42E250B9B2213E217DA999BCF208D91AA7763E2DC0`.
'''
(root/'FPS_STRESS_REPORT.md').write_text(text,encoding='utf-8')
dest=root/'Tools/StressRunner/Measurements'; dest.mkdir(exist_ok=True)
for folder in ['default-final','liquidglass-final']:
 shutil.copyfile(base/folder/'analysis.json',dest/(folder+'.json'))
print(root/'FPS_STRESS_REPORT.md')
