# Chính Sách Bảo Mật — V-Notch

**Ngày hiệu lực:** 31 tháng 8, 2026 (sửa đổi)  
**Phiên bản ứng dụng:** 1.9.1  
**Nhà phát triển:** rainaku  
**Liên hệ:** [github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)  

---

## 1. Giới thiệu

V-Notch là ứng dụng desktop miễn phí, mã nguồn mở dành cho Windows, tái hiện trải nghiệm notch kiểu macOS và Dynamic Island của iPhone. Ứng dụng hiển thị phương tiện đang phát, trạng thái pin và Bluetooth, khay chứa tệp tạm thời (File Shelf), xem trước camera, âm lượng hệ thống và bộ trộn âm thanh từng ứng dụng, màn hình theo dõi tài nguyên phần cứng (CPU, RAM, GPU), đồng hồ, lịch, bộ đếm giờ/bấm giờ, trình tìm kiếm và khởi chạy nhanh Spotlight, cùng các thông tin ngữ cảnh khác với hoạt ảnh mượt mà và hiệu ứng quang học kính lỏng (Liquid Glass) chân thực.

Chính sách bảo mật này giải thích chi tiết: chính xác dữ liệu nào ứng dụng truy cập, tại sao truy cập, dữ liệu đó đi đâu, và được lưu giữ trong bao lâu. Nội dung phản ánh đúng hành vi thực tế của mã nguồn, vốn được công khai hoàn toàn để kiểm tra tại [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

**Nguyên tắc cốt lõi:** V-Notch ưu tiên xử lý cục bộ trên thiết bị của bạn. Ứng dụng không có analytics, không telemetry, không quảng cáo, không theo dõi định danh người dùng, và không yêu cầu tài khoản V-Notch. Ứng dụng không vận hành bất kỳ máy chủ backend riêng nào. Các yêu cầu mạng ra ngoài chỉ phục vụ các mục đích chức năng hoặc tùy chọn cụ thể: kiểm tra cập nhật, lấy ảnh bìa / lời bài hát / phụ đề / Spotify Canvas cho bài hát bạn đang phát, và — nếu bạn chủ động bật — hiển thị dự báo thời tiết. Tất cả được mô tả chi tiết trong Mục 4.

Chính sách này dùng các thuật ngữ sau:
- **"Cục bộ"** — dữ liệu ở lại trên máy của bạn và không bao giờ được gửi đi đâu.
- **"Tạm thời"** — dữ liệu chỉ giữ trong bộ nhớ (RAM/VRAM) trong khi cần để hiển thị hoặc xử lý, sau đó bị loại bỏ ngay lập tức; không bao giờ ghi xuống đĩa.
- **"Tùy chọn (opt-in)"** — tính năng ở trạng thái tắt và không hoạt động cho đến khi bạn chủ động bật hoặc kích hoạt nó.

---

## 2. Tổng quan nhanh

| Khả năng | Truy cập gì | Rời khỏi thiết bị? | Lưu trữ trên đĩa? |
|---|---|---|---|
| **Phương tiện đang phát** | Tên bài, nghệ sĩ, album, ảnh bìa, vị trí phát, trạng thái (Windows SMTC) | Không (trừ tra cứu ảnh bìa/lời/phụ đề — xem §4) | Không (tạm thời trong bộ nhớ) |
| **Tra cứu ảnh bìa album** | Tên bài + nghệ sĩ gửi đi như một truy vấn tìm kiếm | Có — YouTube/Google, SoundCloud, Piped/Invidious | Không (cache trong bộ nhớ và tệp cache nguồn cục bộ) |
| **Lời bài hát đồng bộ** | Tên bài + nghệ sĩ + thời lượng gửi đi như truy vấn | Có — lrclib.net, và api.lrcmux.dev làm nguồn tổng hợp dự phòng | Không (tạm thời trong bộ nhớ) |
| **Phụ đề YouTube / Captions** | Video ID + yêu cầu track phụ đề (YoutubeExplode) | Có — YouTube | Không (tạm thời trong bộ nhớ) |
| **Spotify Canvas (tùy chọn)** | Phiên Spotify web (`sp_dc`), tên bài + nghệ sĩ | Có — Spotify, Musixmatch (dự phòng) | Phiên được mã hóa cục bộ bằng Windows DPAPI |
| **Thời tiết (opt-in)** | Vị trí gần đúng dựa trên IP (`ipwho.is`) hoặc tên thành phố thủ công | Có — `ipwho.is`, Open-Meteo | Không (tạm thời trong bộ nhớ) |
| **Kiểm tra & tải cập nhật** | Chỉ header HTTP tiêu chuẩn | Có — GitHub Releases API | Thông tin phiên bản trong bộ nhớ; bộ cài tải vào thư mục tạm khi cập nhật |
| **Tìm kiếm & Launcher Spotlight** | Tên ứng dụng cục bộ, metadata tệp cục bộ (Windows Search / Everything), biểu thức toán | Không | Lịch sử tần suất khởi chạy lưu cục bộ (tối đa 100 mục, xem §5) |
| **Chụp nền hiệu ứng Liquid Glass** | Pixel màn hình ngay dưới vùng notch (DXGI / Magnification API) | Không | Không (xử lý theo từng frame trên GPU/CPU và giải phóng ngay) |
| **Giám sát phần cứng hệ thống** | Tỷ lệ dùng CPU, mức RAM, tải GPU (Windows performance counters / DXGI) | Không | Không (tạm thời trong bộ nhớ) |
| **Xem trước camera (opt-in)** | Khung hình camera trực tiếp | Không | Không (không bao giờ ghi lại, chụp hay lưu) |
| **File Shelf** | Đường dẫn tệp + metadata tệp cơ bản (tên, kích thước, loại) | Không | Đường dẫn lưu cục bộ trong cài đặt (xem §5) |
| **Âm lượng & Audio Mixer** | Đọc/điều chỉnh âm lượng tổng và từng ứng dụng (Core Audio) | Không | Không |
| **Phát hiện nguồn phát** | Tiêu đề cửa sổ đang hiển thị; URL trình duyệt (UI Automation) | Không | Không (tạm thời trong bộ nhớ; cache nguồn lưu cục bộ) |
| **Bluetooth & trạng thái pin** | Tên thiết bị, loại, mức pin phụ kiện, trạng thái kết nối | Không | Không (tạm thời trong bộ nhớ) |
| **Chỉ báo & Xem trước Clipboard** | Trình lắng nghe định dạng clipboard / sự kiện copy | Không | Không (nội dung clipboard không bao giờ được tải lên hay lưu lại) |
| **Chỉ báo quyền riêng tư** | Micro, camera, hoặc quay màn hình có đang hoạt động không | Không | Không (tạm thời trong bộ nhớ) |
| **Đồng hồ, Lịch, Hẹn giờ** | Giờ hệ thống, bộ đếm ngược, bấm giờ | Không | Cấu hình hẹn giờ lưu trong cài đặt |
| **Cử chỉ chuột** | Di chuyển/nhấp chuột trên vùng notch | Không | Không |
| **Cắt ảnh bìa thông minh** | Nhận diện đối tượng YOLO11n trên thiết bị (ONNX Runtime) | Không | Không (chạy 100% cục bộ) |

---

## 3. Dữ liệu được truy cập trên thiết bị của bạn

### 3.1 Phương tiện đang phát (Windows Media Session)

V-Notch dùng API System Media Transport Controls (SMTC) của Windows để đọc metadata về phương tiện đang phát trên hệ thống — ví dụ từ Spotify, trình phát web YouTube/SoundCloud, Apple Music, Tidal, hoặc bất kỳ tab trình duyệt nào. Metadata bao gồm tên bài, nghệ sĩ, tên album, ảnh bìa nhúng, vị trí phát, thời lượng, và trạng thái phát/tạm dừng.

Dữ liệu này được đọc liên tục khi đang phát, dùng để hiển thị notch theo thời gian thực, và chỉ giữ trong bộ nhớ. Nó không bao giờ được ghi xuống đĩa. Tên bài và nghệ sĩ có thể được gửi tới các dịch vụ bên thứ ba để tra cứu ảnh bìa, lời bài hát, hoặc phụ đề — xem Mục 4.

### 3.2 Phát hiện nguồn phát (Tiêu đề cửa sổ & URL trình duyệt)

Để xác định *nơi* media đang phát (ví dụ phân biệt tab YouTube với tab SoundCloud) và lấy đúng ảnh bìa cùng lời bài hát, V-Notch thực hiện hai kiểu kiểm tra cục bộ:

- **Quét tiêu đề cửa sổ** — Liệt kê tiêu đề của các cửa sổ cấp cao đang hiển thị và chỉ giữ lại những tiêu đề chứa một trong các từ khóa nền tảng cố định: `spotify`, `youtube`, `soundcloud`, `facebook`, `tiktok`, `instagram`, `twitter` / `x`, `apple music`, `apple`, `music`. Các từ khóa nền tảng mạng xã hội mở rộng (Facebook, TikTok, Instagram, Twitter/X) dùng để hỗ trợ phát hiện việc phát video/âm thanh bên trong các tab đó (ví dụ phát TikTok/Reels, như được mô tả trong danh sách tính năng) — chúng chỉ được so khớp với văn bản tiêu đề cửa sổ, hoàn toàn không đọc nội dung trang. Các tiêu đề cửa sổ không khớp sẽ bị loại bỏ ngay lập tức và không bao giờ được giữ lại.
- **Đọc URL trình duyệt** — Với các trình duyệt được hỗ trợ (Chrome, Edge, Firefox, Brave, Opera, Vivaldi, và Zen Browser), ứng dụng dùng API trợ năng UI Automation của Windows để đọc thanh địa chỉ và các tab đang mở nhằm tìm một URL media (liên kết `youtube.com/watch`, `youtu.be`, hoặc `soundcloud.com`). Chỉ những URL là liên kết media mới được xử lý.

Việc kiểm tra này diễn ra hoàn toàn trên thiết bị của bạn. Các tiêu đề và URL được dùng tạm thời để phục vụ phát hiện media và tra cứu ảnh bìa, chỉ được cache ngắn trong bộ nhớ và tệp cache nguồn cục bộ, không bao giờ được lưu xuống đĩa hoặc truyền đi nguyên trạng. (Một giá trị suy ra — tên bài/nghệ sĩ — có thể được gửi đi để tra cứu ảnh bìa như mô tả ở Mục 4.)

### 3.3 Tìm kiếm Spotlight & Trình khởi chạy nhanh (`Alt + Space`)

V-Notch tích hợp trình tìm kiếm Spotlight cho phép bạn tìm ứng dụng, tìm kiếm tệp và tính toán biểu thức toán học nhanh chóng:

- **Tìm kiếm ứng dụng** — Lập chỉ mục các phím tắt Start Menu và các ứng dụng Windows đã cài đặt.
- **Tìm kiếm tệp** — Truy vấn chỉ mục Windows Search Index cục bộ (qua OLE DB) hoặc instance Everything của voidtools đang chạy trên máy (qua Everything IPC socket cục bộ).
- **Máy tính số học tích hợp** — Tính toán các biểu thức số học và đại số trực tiếp trên máy bằng MathNet.Numerics.
- **Bảng xếp hạng tần suất khởi chạy** — Để hiển thị nhanh các mục bạn hay mở, V-Notch lưu một tệp xếp hạng cục bộ (`%APPDATA%\V-Notch\spotlight-usage.json`) chứa ID mục, tên, số lần mở và thời gian gần nhất (giới hạn tối đa 100 mục).

**Toàn bộ quá trình tìm kiếm, truy vấn, đường dẫn tệp, kết quả và phép tính chạy 100% cục bộ trên máy tính của bạn.** Không có từ khóa tìm kiếm hay dữ liệu chỉ mục nào được gửi tới bất kỳ máy chủ bên ngoài nào.

### 3.4 Hiệu ứng Liquid Glass & Chụp nền màn hình

V-Notch trang bị động cơ mô phỏng quang học Liquid Glass giúp tái hiện hiện tượng khúc xạ ánh sáng, tán sắc sắc sai (chromatic aberration), uốn mép (edge bend), vát cạnh (bevel) và làm mờ nền theo phong cách macOS và Dynamic Island.

- Để tính toán khúc xạ quang học, V-Notch lấy mẫu một vùng pixel nhỏ của màn hình ngay phía sau notch bằng DirectX 11 (DXGI Desktop Duplication) hoặc Windows Magnification API.
- **Cam kết bảo mật:** Việc lấy mẫu pixel chỉ diễn ra trong bộ nhớ GPU/CPU cục bộ theo từng khung hình để hiển thị hiệu ứng thị giác. Các khung hình bị loại bỏ ngay lập tức sau khi hiển thị lên màn hình. **Không có nội dung màn hình nào được lưu xuống đĩa, ghi video, chụp ảnh lưu trữ, hoặc truyền tải qua mạng.**

### 3.5 Giám sát tài nguyên phần cứng (CPU, RAM, GPU)

Module System Monitor đọc các thông số hiệu năng phần cứng theo thời gian thực (tỷ lệ phần trăm sử dụng CPU, dung lượng RAM sử dụng, và tải GPU) thông qua Windows performance counters và truy vấn adapter DXGI. Thông tin này chỉ được xử lý tạm thời trong bộ nhớ để hiển thị widget và không bao giờ được lưu trữ hay gửi đi đâu.

### 3.6 Xem trước Camera (Tùy chọn)

V-Notch có thể hiển thị xem trước camera trực tiếp, nhưng chỉ khi bạn chủ động mở tính năng đó. Trong khi hoạt động, các khung hình camera được xử lý cục bộ để hiển thị trên màn hình. **Không có khung hình nào được ghi lại, lưu trữ, chụp ảnh, hoặc truyền đi.** Khi bạn đóng xem trước, camera được giải phóng ngay lập tức. Khi xem trước camera của chính V-Notch đang bật, ứng dụng tự ẩn chấm báo "camera đang dùng" của mình để tránh hiển thị dư thừa.

### 3.7 File Shelf (Tùy chọn)

Khi bạn kéo tệp vào File Shelf, V-Notch ghi lại đường dẫn của mỗi tệp và metadata hệ thống tệp cơ bản (tên, kích thước, loại) để hiển thị và quản lý khay chứa. Ứng dụng dùng `FileSystemWatcher` trên các vị trí đó để giữ khay đồng bộ nếu tệp bị di chuyển hoặc xóa. **Nội dung tệp của bạn không bao giờ bị mở, đọc, sửa đổi, hay truyền đi.** Danh sách đường dẫn tệp được lưu cục bộ trong cài đặt để khay được giữ lại giữa các phiên làm việc (xem Mục 5).

### 3.8 Âm lượng hệ thống & Bộ trộn âm thanh (Audio Mixer)

V-Notch dùng API Windows Core Audio (qua NAudio) để đọc âm lượng tổng của hệ thống, theo dõi phiên âm thanh của từng ứng dụng đang mở, và điều chỉnh mức âm lượng tương ứng khi bạn sử dụng thanh trượt trên notch. Không có âm thanh nào bị ghi âm, thu lại hoặc chặn bắt; chỉ mức âm lượng dạng số và định danh phiên âm thanh của các audio endpoint đang hoạt động được đọc và thiết lập.

### 3.9 Trạng thái thiết bị Bluetooth & Mức pin

V-Notch theo dõi các sự kiện kết nối/ngắt kết nối Bluetooth bằng API liệt kê thiết bị của Windows để hiển thị thông báo kết nối (ví dụ khi tai nghe của bạn kết nối) và mức pin của phụ kiện. Ứng dụng đọc tên hiển thị của thiết bị, loại thiết bị (tai nghe, loa, bàn phím...), trạng thái kết nối và phần trăm pin khi được hỗ trợ. Thông tin này chỉ được dùng tạm thời trên màn hình và không được lưu hay truyền đi.

### 3.10 Chỉ báo & Xem trước Clipboard

V-Notch đăng ký một trình lắng nghe định dạng clipboard của Windows để hiển thị một huy hiệu hoạt ảnh "Copied" ngắn và xem trước nhanh tùy chọn khi clipboard thay đổi. Ứng dụng phản ứng với *sự kiện* clipboard được cập nhật; tính năng này chỉ dùng để phản hồi thị giác và không tải lên, không ghi nhật ký hay lưu trữ nội dung clipboard của bạn.

### 3.11 Chỉ báo quyền riêng tư (Mic / Camera / Quay màn hình)

Mô phỏng hành vi của iOS/macOS, V-Notch có thể hiển thị một chấm màu nhỏ khi micro, camera, hoặc tính năng quay màn hình của bạn đang được *bất kỳ* ứng dụng nào trên hệ thống sử dụng. Đây chỉ là phản ánh trạng thái cảm biến — xử lý tạm thời trong bộ nhớ và không lưu hay truyền tải bất kỳ thông tin nào.

### 3.12 Tiện ích môi trường (Đồng hồ, Lịch, Hẹn giờ & Bấm giờ)

V-Notch tích hợp sẵn các tiện ích hiển thị giờ hệ thống, ngày tháng, giờ quốc tế, lịch tương tác, và bộ đếm ngược / bấm giờ thể thao. Toàn bộ quá trình tính toán và đếm giờ đều chạy hoàn toàn cục bộ trên thiết bị của bạn.

### 3.13 Cử chỉ & Đầu vào chuột

Để hỗ trợ các thao tác cử chỉ vuốt và nhấp đúp trên notch (chuyển/lùi bài, mở khay tệp, phát/tạm dừng), V-Notch theo dõi di chuyển và nhấp chuột trong phạm vi vùng notch. Đầu vào này được diễn giải cục bộ để nhận diện cử chỉ và không bao giờ bị ghi log hay truyền đi.

### 3.14 Cắt ảnh bìa thông minh trên thiết bị (ONNX)

Nếu được bật, V-Notch dùng mô hình nhận diện đối tượng YOLO11n đi kèm, chạy cục bộ qua ONNX Runtime, để tự động cắt ảnh bìa rộng một cách thông minh (canh giữa khuôn mặt hoặc chủ thể bài hát). **Toàn bộ quá trình phân tích ảnh chạy 100% trên thiết bị của bạn. Không có hình ảnh, đầu vào mô hình, hay kết quả nhận diện nào được gửi đi.** Tính năng này không cần kết nối mạng.

---

## 4. Kết nối mạng

V-Notch không có máy chủ backend và không thực hiện analytics, telemetry hay theo dõi người dùng. Ứng dụng chỉ thực hiện yêu cầu ra ngoài tới các dịch vụ bên thứ ba công khai sau đây, và **chỉ** cho các mục đích chức năng được mô tả. Không có định danh thiết bị hay mã theo dõi nào được đính kèm; tính năng Spotify Canvas tùy chọn chỉ dùng phiên Spotify của bạn như mô tả tại Mục 4.5.

### 4.1 Kiểm tra & Tải bản cập nhật ứng dụng — GitHub

- **Điểm đến:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Tại sao:** Để phát hiện xem có phiên bản V-Notch mới hơn hay không.
- **Dữ liệu gửi đi:** Chỉ header HTTP tiêu chuẩn, gồm `User-Agent: V-Notch-Updater` và header `If-None-Match` (ETag) có điều kiện để lưu cache. Không có dữ liệu cá nhân nào được gửi.
- **Dữ liệu nhận về:** Tag phiên bản mới nhất, ghi chú phát hành (changelog), và URL tải xuống bộ cài đặt.
- **Tần suất:** Giới hạn tối đa một lần mỗi 45 giây; phản hồi được cache trong bộ nhớ và xác thực lại bằng ETag.
- **Bảo mật & Tính toàn vẹn:** Việc tải bản cập nhật bắt buộc dùng kết nối HTTPS bảo mật, kiểm tra chữ ký số Authenticode và mã băm toàn vẹn SHA256.
- **Quyền kiểm soát của bạn:** Việc tải và cài đặt bản cập nhật chỉ xảy ra **khi** bạn chủ động chọn thực hiện. Khi bạn bắt đầu cập nhật, bộ cài (`V-Notch-Setup.exe`) được tải từ GitHub Releases về thư mục tạm của bạn và khởi chạy.

### 4.2 Tra cứu ảnh bìa album

Khi SMTC không cung cấp ảnh bìa nhúng (thường gặp khi nghe nhạc trên trình duyệt web), V-Notch cố gắng tìm ảnh bìa tương ứng. Tên bài và nghệ sĩ được dùng làm từ khóa tìm kiếm. Tùy nguồn phát, ứng dụng có thể liên hệ:

**YouTube / Google:**
- `https://www.youtube.com/results?...` — quét trang tìm kiếm công khai để tìm video tương ứng.
- `https://www.youtube.com/oembed?...` — xác thực video và lấy tiêu đề/thumbnail.
- `https://i.ytimg.com/...` — tải ảnh thumbnail.
- `https://www.googleapis.com/youtube/v3/search` — YouTube Data API chính thức, chỉ dùng **nếu** bạn tự cung cấp khóa API cá nhân của bạn. Ứng dụng không đi kèm khóa API có sẵn.

**Piped / Invidious (các front-end YouTube thân thiện quyền riêng tư, dùng làm dự phòng):**
- Các instance công khai như `pipedapi.kavin.rocks`, `pipedapi.adminforge.de`, `vid.puffyan.us`, `invidious.fdn.fr` và tương tự. Đây là các dịch vụ cộng đồng, chỉ được liên hệ nếu tra cứu chính không thành công.

**SoundCloud:**
- Endpoint oEmbed của SoundCloud, để lấy URL ảnh bìa cho bài hát SoundCloud.

**Dữ liệu gửi đi:** Tên bài và nghệ sĩ (như một truy vấn tìm kiếm thông thường) cùng các header HTTP trình duyệt tiêu chuẩn. **Không bao gồm bất kỳ thông tin nhận dạng người dùng nào.** Ảnh tải về được lưu trong bộ nhớ để hiển thị và không được ghi xuống đĩa.

### 4.3 Lời bài hát đồng bộ — LRCLIB và lrc mux

V-Notch thử nghiệm hai nhà cung cấp lời bài hát độc lập theo thứ tự và dừng lại ngay khi một trong hai trả về kết quả:

- **LRCLIB** — `https://lrclib.net/api/get?...` (khớp chính xác) và endpoint tìm kiếm (khớp mờ). **Dữ liệu gửi đi:** tên bài, tên nghệ sĩ, và thời lượng bài hát làm tham số truy vấn, kèm `User-Agent` nhận diện V-Notch.
- **lrc mux** — `https://api.lrcmux.dev/get?...`, dùng làm nguồn tổng hợp dự phòng khi LRCLIB không có kết quả khớp. **Dữ liệu gửi đi:** tên bài, tên nghệ sĩ, và thời lượng bài hát làm tham số truy vấn, kèm `User-Agent` nhận diện V-Notch. lrc mux là dịch vụ tổng hợp lời bài hát của bên thứ ba với các nguồn thượng nguồn riêng; V-Notch không kiểm soát nhà cung cấp thượng nguồn mà dịch vụ này truy vấn nội bộ.

**Dữ liệu nhận về (cả hai):** Các dòng lời bài hát đã đồng bộ thời gian, chỉ dùng tạm thời trong bộ nhớ để hiển thị và không bao giờ ghi xuống đĩa. Không có dữ liệu cá nhân nào được gửi tới cả hai nhà cung cấp.

### 4.4 Phụ đề & Captions YouTube — YoutubeExplode

- **Điểm đến:** Các endpoint phụ đề video công khai của YouTube thông qua thư viện YoutubeExplode.
- **Tại sao:** Để lấy phụ đề/captions theo thời gian thực khi bạn nghe nhạc hoặc xem video trên YouTube và đã bật tính năng phụ đề YouTube.
- **Dữ liệu gửi đi:** ID video YouTube và header HTTP tiêu chuẩn. Không gửi tài khoản người dùng hay dữ liệu định danh cá nhân.
- **Dữ liệu nhận về:** Văn bản phụ đề theo thời gian, chỉ dùng tạm thời trong bộ nhớ để hiển thị.

### 4.5 Spotify Canvas (Tùy chọn)

Khi bạn chọn **Kết nối Spotify**, V-Notch mở trang đăng nhập chính thức của Spotify trong một hồ sơ Microsoft Edge WebView2 tạm thời. Sau khi đăng nhập, ứng dụng chỉ đọc cookie phiên `sp_dc`, xóa hoàn toàn hồ sơ trình duyệt tạm và lưu cookie đã mã hóa bằng Windows DPAPI cho tài khoản người dùng Windows hiện tại. Cookie này không bao giờ được gửi đến bất kỳ máy chủ nào của V-Notch hay máy chủ thu thập dữ liệu nào.

Khi Canvas được bật, phiên được gửi đến Spotify (`open.spotify.com`) để lấy access token ngắn hạn. V-Notch gửi tên bài và nghệ sĩ cùng token đó tới dịch vụ danh mục của Spotify (`api-partner.spotify.com`) để tìm Spotify track ID. Nếu tra cứu này không khả dụng, ứng dụng gửi tên bài, nghệ sĩ và thời lượng tới Musixmatch (`apic-desktop.musixmatch.com`) làm phương án dự phòng. Sau đó, ứng dụng yêu cầu metadata Canvas từ Spotify (`spclient.wg.spotify.com`) và phát video trực tiếp từ mạng phân phối nội dung `*.scdn.co` của Spotify. Các tài nguyên web player Spotify công khai (`open.spotify.com`, `open.spotifycdn.com`) có thể được tải để duy trì tính tương thích của truy vấn; các yêu cầu này không chứa phiên hay metadata bài hát. Secret luân phiên dùng bởi Spotify web player được tải từ kho lưu trữ GitHub công khai `xyloflake/spot-secrets-go`; yêu cầu này không kèm theo dữ liệu người dùng.

Bạn có thể ngắt kết nối Spotify bất kỳ lúc nào trong Cài đặt. Thao tác này sẽ xóa vĩnh viễn phiên đã lưu khỏi V-Notch. Nếu xác thực thất bại hoặc bài hát không có Canvas, ứng dụng tự động chuyển về hình nền lời bài hát thông thường.

### 4.6 Thời tiết (Tùy chọn)

Khi bạn bật widget thời tiết, V-Notch chỉ thực hiện các yêu cầu mạng **sau khi** bạn đã chủ động bật tính năng này. Widget thời tiết **tắt theo mặc định**; không có yêu cầu liên quan đến thời tiết nào được thực hiện khi cài đặt mới cho đến khi bạn bật nó.

- **Định vị dựa trên IP (mặc định):** `https://ipwho.is/` — Vị trí gần đúng (vĩ độ, kinh độ, thành phố) của bạn được xác định từ địa chỉ IP. Đây **không phải** vị trí GPS chính xác; nó là ước lượng địa lý thô dựa trên khu vực đăng ký của dải IP. Chỉ kết nối HTTPS được sử dụng.
- **Nhập thành phố thủ công (tùy chọn):** Nếu bạn nhập tên thành phố thủ công, `https://geocoding-api.open-meteo.com/v1/search` được dùng để phân giải tên thành tọa độ. Khi có thành phố thủ công, hoàn toàn không có tra cứu IP nào được thực hiện.
- **Dự báo thời tiết:** `https://api.open-meteo.com/v1/forecast` — Tọa độ vĩ độ/kinh độ (từ IP hoặc nhập tay) được gửi tới Open-Meteo để lấy nhiệt độ hiện tại, mã thời tiết, nhiệt độ cao/thấp trong ngày và múi giờ.
- **Tần suất:** Mỗi 15 phút khi widget thời tiết đang hoạt động. Các yêu cầu lập tức dừng lại khi bạn tắt tính năng.

Cả hai dịch vụ trên đều là bên thứ ba độc lập với chính sách bảo mật riêng:
- [ipwho.is/privacy](https://ipwho.is/privacy)
- [open-meteo.com/privacy](https://open-meteo.com/privacy)

**Dữ liệu gửi đi:** Địa chỉ IP của bạn (tới ipwho.is), hoặc tên thành phố (tới Open-Meteo geocoding), và tọa độ vĩ độ/kinh độ (tới Open-Meteo forecast). Không có dữ liệu cá nhân nào khác được gửi.

### 4.7 Các bên thứ ba

Các dịch vụ nêu trên (Spotify, GitHub, Google/YouTube, các instance Piped/Invidious, SoundCloud, LRCLIB, ipwho.is, và Open-Meteo) là các bên thứ ba độc lập với chính sách bảo mật riêng của họ. Khi V-Notch liên hệ với họ, địa chỉ IP của bạn tất yếu sẽ hiển thị với dịch vụ đó, như với mọi yêu cầu web thông thường. V-Notch không kiểm soát và không chịu trách nhiệm về cách các dịch vụ đó xử lý yêu cầu. Nếu muốn tránh các tra cứu này, bạn có thể tắt các tính năng ảnh bìa/lời bài hát/phụ đề/Canvas/thời tiết và kiểm tra cập nhật, hoặc chặn truy cập mạng của ứng dụng qua tường lửa.

---

## 5. Lưu trữ dữ liệu cục bộ

Toàn bộ dữ liệu lưu trữ lâu dài do V-Notch tạo ra chỉ nằm duy nhất trên thiết bị của bạn.

### 5.1 Cài đặt (`%APPDATA%\V-Notch\settings.json`)

Lưu các tùy chọn của bạn: kích thước và vị trí notch, kiểu giao diện và tùy chỉnh Liquid Glass, bật/tắt thông báo, ngôn ngữ, hành vi khởi động cùng Windows, đường dẫn tệp trong File Shelf và các cờ tính năng. Tệp cài đặt có thể chứa YouTube API key nếu bạn tự cung cấp và phiên Spotify nếu bạn chọn Kết nối Spotify. Cả hai giá trị nhạy cảm này đều được mã hóa bằng Windows DPAPI (Data Protection API) trước khi ghi xuống đĩa, gắn chặt với tài khoản người dùng Windows hiện tại và không thể bị giải mã bởi người dùng khác hoặc trên máy tính khác. Nếu DPAPI không khả dụng, các giá trị nhạy cảm này sẽ không được lưu.

### 5.2 Lịch sử sử dụng Spotlight (`%APPDATA%\V-Notch\spotlight-usage.json`)

Lưu danh sách các ứng dụng và mục bạn đã mở từ Spotlight (ID, tiêu đề, đường dẫn mục tiêu, số lần mở và mốc thời gian) để xếp hạng gợi ý nhanh. Tệp này giới hạn tối đa 100 mục, lưu cục bộ và không bao giờ được gửi đi đâu.

### 5.3 Bộ nhớ đệm nguồn phát (`%APPDATA%\V-Notch\source_cache.json`)

Lưu ánh xạ theo cơ chế LRU (tối đa 500 mục) giữa tên bài hát đã phát và nguồn media tương ứng (ví dụ YouTube/SoundCloud) để tránh phải tìm kiếm trực tuyến lặp lại cho các bài hát bạn thường xuyên nghe.

### 5.4 Nhật ký chẩn đoán (`vnotch-debug.log`)

Nằm trong thư mục chương trình của ứng dụng, nhật ký này ghi lại các sự kiện kỹ thuật và lỗi phát sinh để hỗ trợ chẩn đoán sự cố. Do tính chất ghi log, tệp này có thể vô tình chứa tên/nghệ sĩ của các bài hát bạn đã nghe, truy vấn tìm lời bài hát, và tiêu đề cửa sổ đã khớp (ví dụ tiêu đề tab trình duyệt) — đây chính là các thông tin đã được mô tả tại Mục 3 và 4, được ghi cục bộ phục vụ gỡ lỗi. Nhật ký tự động xoay vòng khi đạt kích thước khoảng 5 MB. **Nhật ký này không bao giờ được gửi đi đâu** — nó hoàn toàn nằm trên máy của bạn, không tải lên cùng báo cáo lỗi hay yêu cầu cập nhật, và bạn có thể xóa bất kỳ lúc nào.

### 5.5 Mô hình ONNX tùy chọn

Nếu có, tệp mô hình cắt ảnh thông minh (`yolo11n.onnx`) được lưu cục bộ cùng với ứng dụng và chỉ dùng cho mục đích phân tích nhận diện hình ảnh trực tiếp trên thiết bị.

Bạn có thể xóa toàn bộ dữ liệu đã lưu bất cứ lúc nào bằng cách xóa thư mục `%APPDATA%\V-Notch\` và thư mục cài đặt ứng dụng.

---

## 6. Dữ liệu mà V-Notch KHÔNG thu thập

V-Notch **không bao giờ**:
- thu thập, bán, hoặc chia sẻ thông tin cá nhân với bất kỳ bên thứ ba nào;
- chạy các công cụ analytics, telemetry, theo dõi hành vi, hay tạo dấu vân tay thiết bị (fingerprinting);
- tự động gửi báo cáo sự cố hoặc số liệu thống kê sử dụng;
- ghi âm thanh, quay video, hoặc chụp ảnh lưu trữ nội dung màn hình;
- đọc, tải lên, hay sao lưu nội dung bên trong các tệp tin của bạn;
- truy cập tọa độ GPS chính xác của thiết bị;
- tạo tài khoản, hồ sơ cá nhân, hay mã định danh quảng cáo;
- lưu trữ hoặc tải lên nội dung clipboard;
- gửi từ khóa tìm kiếm Spotlight hay dữ liệu chỉ mục tệp qua mạng.

---

## 7. Bảng tham chiếu quyền & API

| Quyền / API | Mục đích | Bắt buộc? |
|---|---|---|
| **Media Session (SMTC)** | Hiển thị metadata phương tiện đang phát & điều khiển phát nhạc | Có (tính năng cốt lõi) |
| **Audio Endpoint (Core Audio)** | Đọc & điều chỉnh mức âm lượng tổng và âm lượng từng ứng dụng | Có (tính năng cốt lõi) |
| **DirectX 11 / DXGI / Magnification** | Lấy mẫu nền màn hình cục bộ cho khúc xạ quang học Liquid Glass | Tùy chọn (hiệu ứng hình ảnh) |
| **Windows Search / Everything IPC** | Tìm kiếm tệp tin và ứng dụng cục bộ trong Spotlight (`Alt + Space`) | Tùy chọn |
| **Truy cập Internet** | Kiểm tra cập nhật, tra cứu ảnh bìa & lời bài hát, dự báo thời tiết | Tùy chọn |
| **Camera (DirectShow / MediaFoundation)** | Xem trước camera trực tiếp ngay trong notch | Opt-in (tùy chọn bật) |
| **Hệ thống tệp (File System)** | Khay chứa tệp tạm thời File Shelf (kéo và thả) | Opt-in (tùy chọn dùng) |
| **UI Automation** | Phát hiện URL phát nhạc trong các trình duyệt web được hỗ trợ | Phục vụ phát hiện media |
| **Bluetooth (Liệt kê thiết bị)** | Thông báo kết nối/ngắt kết nối thiết bị & mức pin phụ kiện | Tùy chọn |
| **Trình lắng nghe Clipboard** | Hoạt ảnh thông báo "Copied" & huy hiệu xem trước | Tùy chọn |
| **Windows Performance Counters** | Đo thông số phần cứng thời gian thực (CPU, RAM, GPU) | Tùy chọn |

---

## 8. Bảo mật

V-Notch hoạt động với quyền người dùng tiêu chuẩn và không yêu cầu quyền quản trị viên (Administrator) trong suốt quá trình hoạt động bình thường. Quyền quản trị viên chỉ được yêu cầu khi thực hiện cài đặt bản cập nhật mới (để chạy trình cài đặt). Tất cả thông tin nhạy cảm lưu trữ (cookie `sp_dc` của Spotify, khóa YouTube API) đều được mã hóa an toàn bằng Windows DPAPI. Mọi bản cập nhật tải về đều được ký số Authenticode và kiểm tra mã băm SHA256 qua kết nối HTTPS bảo mật. Vì ứng dụng hoàn toàn là mã nguồn mở, bất kỳ ai cũng có thể tự do kiểm tra và đánh giá mã nguồn tại [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 9. Quyền riêng tư trẻ em

V-Notch không thu thập dữ liệu cá nhân từ bất kỳ ai, bao gồm trẻ em, và không hướng bất kỳ nội dung nào riêng biệt tới trẻ em. Ứng dụng hoàn toàn an toàn và phù hợp cho mọi lứa tuổi.

---

## 10. Sử dụng quốc tế

V-Notch xử lý dữ liệu cục bộ trên thiết bị của bạn. Dữ liệu duy nhất đi qua mạng là dữ liệu yêu cầu chức năng giới hạn được mô tả trong Mục 4, gửi tới các dịch vụ bên thứ ba công khai, vốn có thể vận hành máy chủ ở nhiều quốc gia khác nhau. Nhà phát triển không thu thập, không chuyển giao và không lưu trữ bất kỳ dữ liệu cá nhân nào của bạn.

---

## 11. Thay đổi chính sách

Chính sách bảo mật này có thể được cập nhật định kỳ khi ứng dụng có thêm tính năng mới. Các thay đổi quan trọng sẽ được phản ánh chi tiết trong tài liệu này, trong changelog của ứng dụng, đồng thời cập nhật ngày hiệu lực và số phiên bản ở đầu tài liệu. Việc bạn tiếp tục sử dụng ứng dụng sau khi cập nhật đồng nghĩa với việc bạn đồng ý với chính sách đã được điều chỉnh.

**Ghi chú sửa đổi (bản cập nhật này):** làm rõ rằng lời bài hát đồng bộ cũng có thể được lấy từ `api.lrcmux.dev` làm phương án dự phòng cho LRCLIB; bổ sung Zen Browser vào danh sách trình duyệt được hỗ trợ phát hiện URL; ghi nhận đầy đủ bộ từ khóa tiêu đề cửa sổ dùng để phát hiện nguồn media (bao gồm Facebook, TikTok, Instagram, và Twitter/X, chỉ dùng để nhận diện phát media trong các tab đó); và làm rõ rằng tệp nhật ký chẩn đoán cục bộ có thể vô tình ghi nhận tên bài hát/tiêu đề cửa sổ đã được đề cập trong chính sách này.

---

## 12. Liên hệ

Mọi câu hỏi, thắc mắc hoặc yêu cầu liên quan đến chính sách và dữ liệu có thể được gửi trực tiếp bằng cách tạo issue tại:  
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
