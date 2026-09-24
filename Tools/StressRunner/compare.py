"""Compare saved stress runs; never substitutes targets for measured throughput."""
import json
import shutil
from pathlib import Path

root = Path(__file__).resolve().parents[2]
base = root / 'artifacts/fps-stress'
names = [('Default', 'default-final', 'default-overload-verified'),
         ('Liquid Glass', 'liquidglass-final', 'liquidglass-overload-verified')]
labels = {'baseline-active': 'Media paused', 'transition-50Hz': 'Spam view',
          'media-1000Hz': 'Flood media', 'mixed-burst': 'Media + view + theme',
          'cpu-contention': 'CPU/RAM contention', 'recovery-active': 'Recovery open',
          'recovery-collapsed': 'Recovery collapsed', 'steady-playing': 'Playing mô phỏng'}
runs = [(style, json.loads((base / before / 'analysis.json').read_text()),
         json.loads((base / after / 'analysis.json').read_text()), after)
        for style, before, after in names]

text = '''# Sửa xử lý quá tải — 24/09/2026

## Thay đổi

- MediaUpdateQueue giữ tối đa một snapshot metadata và một snapshot artwork, chỉ một callback dispatcher đang chờ cho mỗi consumer. Cả ShellViewModel và MainWindow sử dụng queue ở Background priority, nhường input/render. Callback mới thay dữ liệu cũ; không phát lại toàn bộ backlog.
- Thumbnail đến muộn phải khớp track/artist/source/session; ViewModel ghép ảnh vào snapshot hiện tại để không khôi phục timeline/play-state cũ. Queue dừng nhận dữ liệu khi consumer bị dispose.
- Yêu cầu animation được gộp trước khi thực thi ở Input priority; chỉ đích mới nhất còn hợp lệ được chạy. NotchStateManager chấp nhận đảo chiều animation và coi yêu cầu lặp trạng thái là no-op, không phát event lặp. Kiểm tra debug-lock thực hiện tại lúc coordinator nhận yêu cầu, giữ được thao tác chọn view debug vốn tạm mở khóa.
- Liquid Glass chờ UI nhận frame đang pending trước khi capture/upload tiếp, điều chỉnh khoảng frame theo chi phí xử lý trung bình và nhường CPU khi trễ deadline. Không giảm độ phân giải hay đổi cấu hình người dùng.

## Kiểm chứng

Build Release: 0 warnings, 0 errors. `StressRunner --check-overload` kiểm tra 100.000 enqueue từ nhiều producer: một callback pending, giữ metadata mới nhất, thứ tự metadata/artwork, bỏ artwork khác track, cập nhật reentrant, dispose, đảo chiều animation, state idempotent và vẫn chặn cạnh không hợp lệ. Không chạy xUnit/full test suite.

Hai lượt trước đo ngày 23/09; hai lượt sau đo ngày 24/09, cùng kịch bản và máy nhưng tải desktop/thời điểm không được cô lập. Kết quả là đối chiếu các lượt quan sát được, không phải A/B kiểm soát để khẳng định toàn bộ chênh lệch do patch. Hai lượt Default trung gian (`default-overload-fixed`, `default-overload-final`) phục vụ chỉnh sửa gộp animation và chặn callback cũ; không dùng làm kết quả cuối. Hai lượt `overload-verified` dùng cùng DLL cuối, với hash bên dưới.

FPS dưới đây là callback WPF CompositionTarget.Rendering khác RenderingTime, **không phải FPS màn hình đã xác minh**. Frame time là khoảng callback. Probe ở DispatcherPriority.Background không phải đo trực tiếp input latency. Chi tiết môi trường và PresentMon: [báo cáo gốc](FPS_STRESS_REPORT.md).

## Trước → sau

| Theme | Pha | Callback FPS trước → sau | p95 ms trước → sau | Probe max ms trước → sau | Thời gian sau s | Cắt tải sau |
|---|---|---:|---:|---:|---:|---|
'''
for style, before, after, _ in runs:
    old = {p['name']: p for p in before['phases']}
    for p in after['phases']:
        b = old[p['name']]
        text += (f"| {style} | {labels[p['name']]} | {b['callbackFps']:.1f} → {p['callbackFps']:.1f} | "
                 f"{b['frameP95']:.1f} → {p['frameP95']:.1f} | {b['dispatcherMax']:.1f} → {p['dispatcherMax']:.1f} | "
                 f"{p['seconds']:.1f} | {'Có' if p['loadStoppedByWatchdog'] else 'Không'} |\n")

text += '''
| Theme | Peak queue harness trước → sau | Invalid state warnings trước → sau | Peak private MiB trước → sau | Media callbacks sau | View calls sau | Harness bỏ yêu cầu sau | ERROR/render failure sau | Hoàn tất sau |
|---|---:|---:|---:|---:|---:|---:|---|---|
'''
for style, b, a, _ in runs:
    phases = a['phases']
    text += (f"| {style} | {b['peakHarnessQueue']} → {a['peakHarnessQueue']} | "
             f"{b['warnings']['invalidStateTransitions']} → {a['warnings']['invalidStateTransitions']} | "
             f"{max(p['privateMiB']['peak'] for p in b['phases']):.1f} → {max(p['privateMiB']['peak'] for p in phases):.1f} | "
             f"{sum(p['mediaProcessed'] for p in phases):,} | {sum(p['transitionCalls'] for p in phases):,} | "
             f"{sum(p['droppedByHarness'] for p in phases)} | {a['warnings']['errors']}/{a['warnings']['renderFailed']} | {a['completedFile']} |\n")

text += '\n## Kết quả chịu tải và giới hạn\n\n'
for style, _, a, folder in runs:
    cpu = next(p for p in a['phases'] if p['name'] == 'cpu-contention')
    stopped = [p['name'] for p in a['phases'] if p['loadStoppedByWatchdog']]
    text += (f"- {style}: CPU/RAM đạt {cpu['callbackFps']:.1f} callback FPS, p95 {cpu['frameP95']:.1f} ms, "
             f"probe max {cpu['dispatcherMax']/1000:.3f} s. "
             + (f"Watchdog cắt nguồn tải ở: {', '.join(stopped)}." if stopped else 'Không cần watchdog cắt nguồn tải trong các pha đã chạy.') + '\n')
    binary = json.loads((base / folder / 'binary.json').read_text(encoding='utf-8-sig'))
    text += f"  DLL SHA256: `{binary['Hash']}`.\n"

text += '''
Hàng đợi harness khác queue nội bộ của app. Các con số media là số lời gọi được đưa tới handler, không phải số cập nhật được vẽ; coalescing cố ý bỏ snapshot trung gian. Số view calls cũng không phải animation hoàn tất. Bài stress hiện gọi trực tiếp Media/Progress ViewModel rồi MainWindow, nên phép đo UI không đo riêng chi phí đường event của ShellViewModel; kiểm tra queue dùng implementation production.

Mỗi lượt chỉ vài phút, có profile riêng và media mô phỏng. Đây không phải soak 30–60 phút hay chứng minh không leak. Không suy rộng sang Spotify/network, thiết bị thực, GPU reset, sleep/resume hay độ mượt cảm nhận. Cần người dùng kiểm tra thao tác thật: đảo chiều mở/đóng nhanh, chuyển Media/Timer/Audio/Shelf, thumbnail đến muộn khi đổi track/seek và độ bám chuyển động của Glass.

## Tái lập

Đọc `Tools/StressRunner/README.md`. Dữ liệu gốc sau sửa ở `artifacts/fps-stress/default-overload-verified/` và `artifacts/fps-stress/liquidglass-overload-verified/`. JSON tổng hợp được lưu ở `Tools/StressRunner/Measurements/`. Chạy `analyze.py` cho từng thư mục rồi `compare.py` để tạo lại báo cáo này. Không ghi đè các lượt trước sửa.
'''
(root / 'OVERLOAD_FIX_REPORT.md').write_text(text, encoding='utf-8')
for _, _, _, folder in runs:
    shutil.copyfile(base / folder / 'analysis.json', root / 'Tools/StressRunner/Measurements' / (folder + '.json'))
print(root / 'OVERLOAD_FIX_REPORT.md')
