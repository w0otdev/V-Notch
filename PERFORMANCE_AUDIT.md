# Kiểm tra hiệu năng Release — 22/10/2026

Đã sửa các đường xử lý nền khi notch thu gọn, cache settings/update, bộ nhớ lịch sử frame, cấp phát Visualizer và công cụ đo. Build và publish `Release / win-x64` thành công. Các số dưới đây là phép đo thành phần; chưa có phép đo lại toàn tiến trình với đúng kịch bản YouTube và màn hình của báo cáo ban đầu.

Workspace đã có thay đổi chưa commit trước khi bắt đầu. `render-before.json` và `report-before.json` đo trạng thái workspace đó, không phải bản `HEAD` sạch. Không hoàn tác các thay đổi có sẵn.

| Mục trong báo cáo | Thay đổi và phạm vi đã xử lý |
|---|---|
| 1. LiquidGlass | Chỉ dùng full surface khi chuyển động; giảm padding vùng capture ở trạng thái ổn định. Khi collapsed, không animation và backdrop không đổi ít nhất 250 ms, chu kỳ kiểm tra tăng lên tối thiểu 100 ms. Tương tác/animation đánh thức worker và bỏ deadline idle cũ. Presentation cắt ảnh D3D theo vùng capture trước khi WPF rasterize đầu vào shader, giữ mật độ pixel. Bỏ ép upload mỗi 500 ms; phục hồi front buffer yêu cầu refresh bằng sự kiện. Surface D3D vẫn giữ capacity ổn định cho animation. |
| 2. AI và blur artwork | Không bắt đầu/tung kết quả blur khi collapsed, glass hoặc tính năng tắt. Guard ảnh trùng chạy trước blur và dim animation. Detections đã cache trên ảnh gốc được ánh xạ sang `CroppedBitmap`, gồm cả kết quả không có subject. Mã crop đồng bộ hiện tại vốn dùng saliency; không giả định mọi bài hát trước đây đều suy luận ONNX hai lần. |
| 3. Visualizer | Giữ drawing/geometry của bars và morph; cập nhật tài nguyên thay vì ghi lại drawing của UIElement mỗi frame. Tái sử dụng các giá trị Rect đã snap theo layout/DPI. Giảm cấp phát, còn cấp phát trong `StreamGeometry.Open` khi morph thay đổi. Thời gian raster offscreen tăng ở các case đo được, xem bảng dưới. |
| 4. Frame history | Thay snapshot toàn frame bằng XXH128 SIMD trên toàn bộ byte và kích thước ảnh. Giữ digest, không giữ pixel. Commit dùng digest vừa kiểm tra, không quét/copy lần hai. Đây là fingerprint 128 bit, không phải bằng chứng so sánh byte tuyệt đối không thể collision. |
| 5. Startup | Kiểm tra single instance trước DI/settings/localization cho startup thông thường; bỏ gọi CrashReporter trùng. Hiện MainWindow trước log-session I/O và prewarm. Bỏ resolve đồng bộ hàng loạt singleton; bỏ warmup AudioMixer/Bluetooth không cần thiết ở đây. |
| 6. Settings | Cache snapshot theo file length/LastWriteTimeUtc; trả clone độc lập. Save thành công cập nhật cache; thay đổi file ngoài tiến trình làm mất cache. Đọc lỗi tạm thời không cache mặc định. Giới hạn 10 backup vốn đã có khi save; bổ sung dọn lịch sử cũ ngay lúc load từ đĩa. |
| 7. Lyrics/subtitles/Canvas | Chỉ bắt đầu tải lời/phụ đề khi mở đúng bề mặt; Canvas thêm điều kiện expanded vào predicate đã có. Ghi nhớ request phụ đề theo track, gồm kết quả rỗng, tránh tải lại theo mỗi media update/expand. Đổi track xóa lời cũ và vô hiệu kết quả async cũ. Explicit refetch vẫn dùng được. Không loại bỏ các cập nhật position/media cần thiết. |
| 8. Updater | Lưu LastCheckUtc, ETag, release đã kiểm tra và thời hạn backoff vào `update-cache.json`. Freshness 1 giờ áp dụng cả kết quả rỗng; 304 cập nhật cache. 403/429 dùng Retry-After/rate-limit reset, có giới hạn và tồn tại qua restart. Cache không bỏ qua kiểm tra URL tải hay chữ ký cập nhật. |
| 9. Timer/polling | Không khởi động privacy từ prewarmer; module quản lý lifecycle. Workspace hiện tại đã dùng scheduler chung cho `UiScheduledTimer` và có cache/điều kiện chạy privacy worker. Số object timer không đồng nghĩa số timer hệ điều hành. Chưa thay toàn bộ polling bằng event và chưa xác nhận giảm tổng thread xuống một mức cụ thể. |
| 10. Resources | Bỏ bản copy rời Locales, Terms và shader đã nhúng. Chuyển 4 font SF Pro thành content ngoài single-file bundle, cập nhật mọi font URI/markup. Giữ `logo.ico` rời và ngoài bundle vì tray đọc từ đĩa; nó khác resource logo WPF. Installer hiện có lấy đệ quy thư mục publish. |
| 11. Cây XAML | ClipboardTray được tạo một lần khi dùng lần đầu; gắn controller, binding, localization và FPS khi tạo. Cleanup/shadow/menu không vô tình tạo view. Các view phụ khác vẫn còn trong XAML; chưa chuyển toàn bộ MainWindow/SettingsWindow thành lazy view. |
| 12. Công cụ đo | Benchmark đồng hồ luôn UpdateFrame ở frame đầu; baseline dùng brush và font hiện tại. ReportBenchmark giữ so sánh baseline lịch sử, thêm oracle tuyến tính dùng HSL đã sửa để kiểm tra thuật toán contrast. Script xuất UTF-8 không BOM và Summary mean/p95/max cho CPU, RAM, từng GPU engine. Không cộng các engine thành một phần trăm GPU tổng. |

`DiagnosticLogEntry` chuyển thành readonly record struct, bỏ allocation mỗi lần thêm log vào buffer. Palette dùng lại trọng số không gian 36×36 và bỏ closure log ở cache miss; cold palette vẫn chậm hơn baseline lịch sử trong lượt đo này.

**Số đo đã lưu**

Môi trường: Windows 10.0.26200, .NET 8.0.29, x64, 16 logical processors. Không chạy test/build đồng thời với các lượt benchmark cuối. Kết quả raw nằm trong `artifacts/perf-audit/` (thư mục bị Git ignore).

| Thành phần | Trước thay đổi trong lượt này | Sau thay đổi | Diễn giải |
|---|---:|---:|---|
| Visualizer bars, 96 DPI, update | 968 B/frame; 0,936 µs | 94,16 B/frame; 1,036 µs | Giảm khoảng 90% allocation, update lâu hơn 10,7%. |
| Visualizer morph, 96 DPI, update | 1.724,4 B/frame; 3,724 µs | 989,59 B/frame; 4,170 µs | Giảm khoảng 43% allocation, update lâu hơn 12%. |
| Visualizer icon, 96 DPI, update | 1.568 B/frame; 2,817 µs | 984 B/frame; 2,423 µs | Giảm khoảng 37% allocation và thời gian update. |
| Visualizer snapped, 96 DPI | 0 B/frame | 0 B/frame | Giữ suppression khi cạnh pixel chưa đổi. |
| Visualizer bars, 96 DPI, raster | 64,435 µs | 96,610 µs | Chậm hơn 49,9%; đây là raster offscreen đồng bộ, chưa chứng minh GPU/display chậm tương ứng. Các case raster khác cũng tăng khoảng 25–50%. |
| History 1520×1370, static | 531,083 µs; 7,94 MiB snapshot | 475,640 µs; 0 pixel bytes giữ lại | 0 B/frame. |
| History 1520×1370, small motion | 454,569 µs | 489,146 µs | Chậm hơn khoảng 7,6%; không khẳng định hash nhanh hơn ở mọi case. |
| History 1520×1370, late motion | 1.295,536 µs | 521,154 µs | Giảm khoảng 60% thời gian, tránh compare gần hết rồi copy. |
| Service log full buffer | 48 B/op | 0 B/op | Thời gian 0,066 → 0,084 µs; không tuyên bố nhanh hơn. |
| Palette + dim cold | 2.440,68 B/op; 253,210 µs | 2.192,68 B/op; 254,651 µs | Allocation giảm; chưa giải quyết được cold latency. Baseline lịch sử của lượt cuối: 211,994 µs. |

Các cột `before` của GlassMotionBenchmark là fingerprint lấy mẫu cũ: khoảng 8 µs ở frame lớn nhưng bỏ sót 240/240 chuyển động nhỏ. Để so sánh snapshot trước/sau của lượt sửa này phải lấy cột `after` giữa `glass-motion-before.txt` và `glass-motion-simd.txt`. Hash mới bỏ sót 0/240 ở các fixture; đổi từ quét toàn frame sang lấy mẫu không được coi là tối ưu tương đương độ chính xác.

FFT không tái hiện regression được nêu: benchmark hiện tại cho 6,727 → 6,221 µs tại 44.100 Hz và 6,222 → 6,017 µs tại 192.000 Hz, 0 B/frame. Không sửa thuật toán FFT trong lượt này. Đây là so sánh baseline đã có với production hiện tại trong `fft-before.json`.

DefaultThemeBenchmark chạy xong 256 phép so sánh pixel. Update đồng hồ khoảng 2,05–2,44 → 0,47–0,59 µs, 1.936 → 72 B/frame. Raster có case tốt hơn và kém hơn; không suy ra tăng FPS toàn app. ReportBenchmark cho `SameBlurOutput=true`, `SameContrastOutput=true`, đồng thời giữ `MatchesHistoricalContrastOutput=false` vì baseline HSL lịch sử có lỗi wrap channel. Không đổi màu production để khớp lỗi đó.

Visualizer khớp byte pixel trong 864 trường hợp trước/sau. Các case có tên lặp để kiểm tra quay lại trạng thái bars; so sánh theo thứ tự đầy đủ, không gộp bằng tên case. DLL build hiện tại 6.497.280 byte (~6,20 MiB). 4 font vẫn được phân phối trong `Fonts`, không suy ra tổng RAM giảm 9 MiB.

**Kiểm chứng và phần còn lại**

- Build hai benchmark và publish Release: 0 warning/error; publish có 4 OTF và icon rời, không có Locales/Terms/shader copy thừa.
- Regression tập trung cuối: 69/69 đạt, gồm cache, 403/304/restart, font đủ bốn weight, crop detection reuse, idle/wake, capture pixel density ở 3 DPI, lyrics collapsed/negative cache và Clipboard first use.
- Lượt mở rộng các nhóm bị tác động: 865 đạt, 0 lỗi, 5 desktop integration bị skip / 870. Filter bao gồm settings/update/lyrics/glass/Clipboard/clock/Visualizer rendering; loại rõ ba nhóm đang lỗi ở lượt toàn bộ (`MainWindowSettingsIntegrationTests`, `MainWindowUpdateNotificationTests`, `GlassBackdropAlignmentTests`). Không diễn giải lượt này thành toàn suite đã xanh.
- Kiểm tra script trên Windows PowerShell 5.1: JSON không BOM; Summary có SampleCount/Mean/P95. Đây là smoke test trên PowerShell, không phải số đo V-Notch.
- Lượt toàn bộ trước hai sửa cuối của test/lazy Clipboard: 3.927 đạt, 31 lỗi, 6 bỏ qua / 3.964. Các lỗi thuộc Clipboard visibility và 5 case nạp AppFont từ XAML fragment đã được sửa và qua lượt 69 test. Các lỗi khác có reflection signature cũ, motion timeout/layout, settings preview cancel, metadata, Unicode number và subject blur; chưa có baseline toàn suite sạch để gọi tất cả là lỗi có sẵn.
- Chưa có kết quả CPU/GPU/RAM trước/sau của tiến trình thật trong kịch bản collapsed 230×38 + YouTube. Các desktop integration test cần tương tác màn hình đang được skip theo cấu hình test.

Raw artifacts chính: `render-before.json`, `render-final.json`, `report-before.json`, `report-final.json`, `default-final.json`, `glass-motion-before.txt`, `glass-motion-simd.txt`, `fft-before.json`, và TRX trong `tests/`. `render-retained.json`, `render-frozen-bars.json` và `glass-motion-after.txt` là thử nghiệm trung gian đã loại, không đại diện mã cuối.

Đo lại tiến trình thật với cùng workload, DPI và refresh rate:

```powershell
./scripts/Measure-AppPerformance.ps1 -AppProcessId <PID> -WarmupSeconds 10 -DurationSeconds 60 -OutputPath artifacts/perf-audit/collapsed-youtube.json
```

Font content được WPF hỗ trợ theo [tài liệu đóng gói font của Microsoft](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/packaging-fonts-with-applications). Giữ thư mục `Fonts` cạnh executable khi phát hành.
