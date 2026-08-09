# Chính Sách Bảo Mật — V-Notch

**Ngày hiệu lực:** 29 tháng 5, 2026
**Phiên bản ứng dụng:** 1.7.4
**Nhà phát triển:** rainaku
**Liên hệ:** [github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)

---

## 1. Giới thiệu

V-Notch là ứng dụng desktop miễn phí, mã nguồn mở dành cho Windows, tái hiện trải nghiệm notch / Dynamic Island kiểu macOS. Ứng dụng hiển thị phương tiện đang phát, trạng thái pin và Bluetooth, một khay tệp (File Shelf), xem trước camera, âm lượng hệ thống, và các thông tin ngữ cảnh khác.

Chính sách bảo mật này giải thích chi tiết: chính xác dữ liệu nào ứng dụng truy cập, tại sao truy cập, dữ liệu đó đi đâu, và được lưu giữ trong bao lâu. Nội dung phản ánh đúng hành vi thực tế của mã nguồn, vốn được công khai để kiểm tra tại [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

**Nguyên tắc cốt lõi:** V-Notch ưu tiên xử lý cục bộ. Ứng dụng không có analytics, không telemetry, không quảng cáo, và không yêu cầu tài khoản V-Notch. Ứng dụng không vận hành máy chủ riêng. Các yêu cầu mạng ra ngoài chỉ phục vụ một số mục đích: kiểm tra cập nhật, lấy ảnh bìa / lời bài hát / Spotify Canvas cho phương tiện bạn đang phát, và — nếu bạn chủ động chọn — hiển thị thời tiết. Tất cả được mô tả trong Mục 4.

Chính sách này dùng các thuật ngữ sau:
- **"Cục bộ"** — dữ liệu ở lại trên máy của bạn và không bao giờ được gửi đi đâu.
- **"Tạm thời"** — dữ liệu chỉ giữ trong bộ nhớ trong khi cần để hiển thị, sau đó bị loại bỏ; không bao giờ ghi xuống đĩa.
- **"Tùy chọn (opt-in)"** — tính năng không làm gì cho đến khi bạn chủ động bật hoặc kích hoạt nó.

---

## 2. Tổng quan nhanh

| Khả năng | Truy cập gì | Rời khỏi thiết bị? | Lưu trữ? |
|---|---|---|---|
| Phương tiện đang phát | Tên bài, nghệ sĩ, ảnh bìa, vị trí, trạng thái phát (Windows SMTC) | Không (trừ tra cứu ảnh bìa/lời — xem §4) | Không (tạm thời) |
| Tra cứu ảnh bìa album | Tên bài + nghệ sĩ gửi đi như một truy vấn tìm kiếm | Có — YouTube/Google, SoundCloud, Piped/Invidious | Không (ảnh cache trong bộ nhớ) |
| Lời bài hát đồng bộ | Tên bài + nghệ sĩ + thời lượng gửi đi như truy vấn | Có — lrclib.net | Không (tạm thời) |
| Spotify Canvas (tùy chọn) | Phiên Spotify, tên bài + nghệ sĩ | Có — Spotify | Phiên được mã hóa cục bộ bằng Windows DPAPI |
| Thời tiết (opt-in) | Vị trí gần đúng dựa trên IP (ipwho.is) hoặc tên thành phố thủ công (geocoding) | Có — ipwho.is, Open-Meteo | Không (tạm thời) |
| Kiểm tra cập nhật | Chỉ header HTTP tiêu chuẩn | Có — GitHub API | Thông tin phiên bản cache trong bộ nhớ |
| Xem trước camera | Khung hình camera trực tiếp | Không | Không (không bao giờ ghi lại) |
| File Shelf | Đường dẫn tệp + metadata cơ bản | Không | Đường dẫn lưu cục bộ (xem §5) |
| Âm lượng hệ thống | Đọc/điều chỉnh mức audio endpoint | Không | Không |
| Phát hiện nguồn phát | Tiêu đề cửa sổ đang hiển thị; URL trình duyệt | Không | Không (tạm thời) |
| Trạng thái Bluetooth | Tên thiết bị, loại, trạng thái kết nối | Không | Không (tạm thời) |
| Chỉ báo clipboard | Sự kiện *thay đổi* clipboard (không phải nội dung) | Không | Không |
| Chỉ báo quyền riêng tư | Mic/camera/quay màn hình có đang hoạt động không | Không | Không (tạm thời) |
| Cử chỉ | Di chuyển/nhấp chuột trên vùng notch | Không | Không |
| Cắt ảnh bìa thông minh | Phân tích ảnh trên thiết bị (ONNX) | Không | Không |

---

## 3. Dữ liệu được truy cập trên thiết bị của bạn

### 3.1 Phương tiện đang phát (Windows Media Session)

V-Notch dùng API System Media Transport Controls (SMTC) của Windows để đọc metadata về phương tiện đang phát trên hệ thống — ví dụ từ Spotify, trình phát web YouTube/SoundCloud, Apple Music, hoặc bất kỳ tab trình duyệt nào. Metadata bao gồm tên bài, nghệ sĩ, tên album, ảnh bìa nhúng, vị trí phát, thời lượng, và trạng thái phát/tạm dừng.

Dữ liệu này được đọc liên tục khi đang phát, dùng để hiển thị notch theo thời gian thực, và chỉ giữ trong bộ nhớ. Nó không bao giờ được ghi xuống đĩa. Tên bài và nghệ sĩ có thể được gửi tới các dịch vụ bên thứ ba để tra cứu ảnh bìa và lời bài hát — xem Mục 4.

### 3.2 Phát hiện nguồn phát (Tiêu đề cửa sổ & URL trình duyệt)

Để xác định *nơi* media đang phát (ví dụ phân biệt tab YouTube với tab SoundCloud) và lấy đúng ảnh bìa, V-Notch thực hiện hai kiểu kiểm tra cục bộ:

- **Quét tiêu đề cửa sổ** — Liệt kê tiêu đề của các cửa sổ cấp cao đang hiển thị và tìm các từ khóa media đã biết (như "spotify", "youtube", "soundcloud", "apple music"). Chỉ những tiêu đề khớp từ khóa mới được giữ lại, trong thời gian ngắn, trong bộ nhớ.
- **Đọc URL trình duyệt** — Với các trình duyệt được hỗ trợ (Chrome, Edge, Firefox, Brave, Opera, Vivaldi), ứng dụng dùng API trợ năng UI Automation của Windows để đọc thanh địa chỉ và, nếu cần, các tab đang mở, nhằm tìm một URL media (liên kết `youtube.com/watch`, `youtu.be`, hoặc `soundcloud.com`). Chỉ những URL trông giống liên kết media mới được dùng.

Việc kiểm tra này diễn ra hoàn toàn trên thiết bị của bạn. Các tiêu đề và URL được dùng tạm thời để phục vụ phát hiện media và tra cứu ảnh bìa, chỉ được cache ngắn trong bộ nhớ, và không bao giờ được lưu xuống đĩa hoặc truyền đi nguyên trạng. (Một giá trị suy ra — tên bài/nghệ sĩ — có thể được gửi đi để tra cứu ảnh bìa như mô tả ở Mục 4.)

### 3.3 Camera (Tùy chọn)

V-Notch có thể hiển thị xem trước camera trực tiếp, nhưng chỉ khi bạn chủ động mở tính năng đó. Trong khi hoạt động, các khung hình camera được xử lý cục bộ để hiển thị trên màn hình. **Không có khung hình nào được ghi lại, lưu, chụp, hoặc truyền đi.** Khi bạn đóng xem trước, camera được giải phóng. Khi xem trước camera của chính V-Notch đang bật, ứng dụng tự ẩn chấm báo "camera đang dùng" của mình để tránh chỉ báo dư thừa.

### 3.4 File Shelf (Tùy chọn)

Khi bạn kéo tệp vào File Shelf, V-Notch ghi lại đường dẫn của mỗi tệp và metadata hệ thống tệp cơ bản (tên, kích thước, loại) để hiển thị và quản lý khay. Ứng dụng dùng một `FileSystemWatcher` trên các vị trí đó để giữ khay đồng bộ nếu tệp bị di chuyển hoặc xóa. **Nội dung tệp của bạn không được mở, đọc, sửa đổi, hoặc truyền đi.** Danh sách đường dẫn tệp được lưu cục bộ để khay được giữ lại giữa các phiên (xem Mục 5).

### 3.5 Âm lượng hệ thống

V-Notch dùng API Windows Core Audio (qua NAudio) để đọc âm lượng hệ thống hiện tại và điều chỉnh khi bạn dùng nút điều khiển âm lượng trên notch. Không có âm thanh nào được ghi hoặc thu lại; chỉ mức âm lượng dạng số của audio endpoint mặc định được đọc và đặt.

### 3.6 Trạng thái thiết bị Bluetooth

V-Notch theo dõi các sự kiện kết nối/ngắt kết nối Bluetooth bằng API liệt kê thiết bị của Windows để hiển thị thông báo kết nối (ví dụ, khi tai nghe của bạn kết nối). Ứng dụng đọc tên hiển thị của thiết bị, một dự đoán về loại (tai nghe, loa, bàn phím, v.v.), và trạng thái kết nối. Thông tin này được dùng tạm thời cho thông báo trên màn hình và không được lưu hoặc truyền đi.

### 3.7 Chỉ báo thay đổi Clipboard

V-Notch đăng ký một *trình lắng nghe định dạng* clipboard của Windows để hiển thị một hoạt ảnh xác nhận "Copied" ngắn khi clipboard thay đổi. Ứng dụng phản ứng với *sự kiện* clipboard được cập nhật; tính năng này chỉ dùng để kích hoạt một hiệu ứng chớp trực quan và không tải lên hay lưu giữ dữ liệu clipboard.

### 3.8 Chỉ báo quyền riêng tư (Mic / Camera / Quay màn hình)

Mô phỏng hành vi iOS/macOS, V-Notch có thể hiển thị một chấm màu nhỏ khi micro, camera, hoặc quay màn hình của bạn đang được *bất kỳ* ứng dụng nào sử dụng. Đây chỉ là phản ánh trạng thái — nó cho biết một cảm biến đang hoạt động, xử lý trạng thái đó tạm thời trong bộ nhớ, và không lưu hay truyền đi gì.

### 3.9 Cử chỉ & Đầu vào chuột

Để hỗ trợ cử chỉ vuốt và nhấp đúp trên notch (bài kế/trước, mở khay, phát/tạm dừng), V-Notch theo dõi di chuyển và nhấp chuột trong vùng của notch. Đầu vào này được diễn giải cục bộ để nhận diện cử chỉ và không bao giờ được ghi log hay truyền đi.

### 3.10 Cắt ảnh bìa thông minh trên thiết bị (ONNX)

Nếu được bật, V-Notch dùng một mô hình nhận diện đối tượng YOLO11n đi kèm, chạy cục bộ qua ONNX Runtime, để cắt ảnh bìa rộng một cách thông minh (canh giữa khuôn mặt hoặc chủ thể). **Toàn bộ phân tích ảnh chạy hoàn toàn trên thiết bị của bạn. Không có ảnh, đầu vào mô hình, hoặc kết quả nhận diện nào được gửi đi đâu.** Tính năng này không cần kết nối mạng.

---

## 4. Kết nối mạng

V-Notch không có máy chủ backend và không thực hiện analytics hay telemetry. Ứng dụng chỉ thực hiện yêu cầu ra ngoài tới các dịch vụ bên thứ ba công khai sau đây, và **chỉ** cho các mục đích được mô tả. Không có định danh thiết bị hay token theo dõi; tính năng Spotify Canvas tùy chọn chỉ dùng phiên Spotify như mô tả tại Mục 4.4.

### 4.1 Kiểm tra cập nhật ứng dụng — GitHub

- **Điểm đến:** `https://api.github.com/repos/rainaku/V-Notch/releases/latest`
- **Tại sao:** Để phát hiện xem có phiên bản V-Notch mới hơn hay không.
- **Dữ liệu gửi đi:** Chỉ header HTTP tiêu chuẩn, gồm `User-Agent: V-Notch-Updater` và một header `If-None-Match` (ETag) có điều kiện để cache. Không có dữ liệu cá nhân nào được gửi.
- **Dữ liệu nhận về:** Tag phiên bản mới nhất, ghi chú phát hành, và URL tải xuống bộ cài.
- **Tần suất:** Giới hạn tối đa một lần mỗi 45 giây; phản hồi được cache trong bộ nhớ và xác thực lại bằng ETag.
- **Quyền kiểm soát của bạn:** Việc tải và cài đặt bản cập nhật chỉ xảy ra **khi** bạn chủ động chọn. Nếu bạn bắt đầu cập nhật, bộ cài (`V-Notch-Setup.exe`) được tải từ URL tài nguyên của bản phát hành GitHub về thư mục tạm của bạn rồi chạy.

### 4.2 Tra cứu ảnh bìa album

Khi SMTC không cung cấp ảnh bìa nhúng (thường gặp với phát qua trình duyệt), V-Notch cố tìm một ảnh bìa khớp. Tên bài và nghệ sĩ được dùng làm từ khóa tìm kiếm. Tùy nguồn, ứng dụng có thể liên hệ:

**YouTube / Google:**
- `https://www.youtube.com/results?...` — quét trang tìm kiếm công khai để tìm video khớp.
- `https://www.youtube.com/oembed?...` — xác thực một video và lấy tiêu đề/thumbnail.
- `https://i.ytimg.com/...` — lấy ảnh thumbnail.
- `https://www.googleapis.com/youtube/v3/search` — YouTube Data API chính thức, chỉ dùng **nếu** bạn đã cung cấp khóa API của riêng bạn. Không có khóa nào đi kèm trong ứng dụng.

**Piped / Invidious (các front-end YouTube thân thiện quyền riêng tư, dùng làm phương án dự phòng):**
- Các instance công khai như `pipedapi.kavin.rocks`, `pipedapi.adminforge.de`, `vid.puffyan.us`, `invidious.fdn.fr`, và tương tự. Đây là các dịch vụ do cộng đồng vận hành, chỉ được liên hệ nếu tra cứu chính thất bại.

**SoundCloud:**
- Điểm cuối oEmbed của SoundCloud, để lấy URL ảnh bìa cho một bài SoundCloud.

**Dữ liệu gửi đi:** tên bài và nghệ sĩ (như một truy vấn tìm kiếm) và các header HTTP giống trình duyệt tiêu chuẩn. **Không có thông tin nhận dạng người dùng nào được bao gồm.** Ảnh lấy về được giữ trong bộ nhớ để hiển thị và không được ghi xuống đĩa.

### 4.3 Lời bài hát đồng bộ — LRCLIB

- **Điểm đến:** `https://lrclib.net/api/get?...`
- **Tại sao:** Để lấy lời bài hát đồng bộ theo thời gian cho bài hiện tại, khi tính năng lời bài hát được dùng.
- **Dữ liệu gửi đi:** Tên bài, tên nghệ sĩ, và thời lượng bài làm tham số truy vấn, cùng một `User-Agent` nhận diện V-Notch. Không có dữ liệu cá nhân nào được gửi.
- **Dữ liệu nhận về:** Các dòng lời đồng bộ, dùng tạm thời để hiển thị.

### 4.4 Spotify Canvas (Tùy chọn)

Khi bạn chọn **Kết nối Spotify**, V-Notch mở trang đăng nhập chính thức của Spotify trong một hồ sơ Microsoft Edge WebView2 tạm thời. Sau khi đăng nhập, ứng dụng chỉ đọc cookie phiên `sp_dc`, xóa hồ sơ trình duyệt tạm và lưu cookie đã mã hóa bằng Windows DPAPI cho tài khoản Windows hiện tại. Cookie này không được gửi đến máy chủ V-Notch hoặc PaxSenix.

Khi Canvas được bật, phiên được gửi đến Spotify (`open.spotify.com`) để lấy access token ngắn hạn. V-Notch gửi tên bài và nghệ sĩ cùng token đó tới dịch vụ danh mục của Spotify (`api-partner.spotify.com`) để tìm Spotify track ID. Nếu tra cứu này không khả dụng, ứng dụng gửi tên bài, nghệ sĩ và thời lượng tới Musixmatch (`apic-desktop.musixmatch.com`) làm phương án dự phòng. Sau đó, ứng dụng yêu cầu metadata Canvas từ Spotify (`spclient.wg.spotify.com`) và phát video từ mạng phân phối nội dung `*.scdn.co` của Spotify. Các tài nguyên Spotify web player công khai (`open.spotify.com`, `open.spotifycdn.com`) có thể được tải để duy trì tính tương thích của truy vấn danh mục; các yêu cầu làm mới này không chứa phiên hay metadata bài hát. Secret luân phiên dùng bởi Spotify web player được tải từ repository GitHub công khai `xyloflake/spot-secrets-go`; yêu cầu này không kèm dữ liệu người dùng.

Bạn có thể ngắt kết nối Spotify bất kỳ lúc nào trong Cài đặt. Thao tác này xóa phiên đã lưu khỏi V-Notch. Nếu xác thực thất bại hoặc bài hát không có Canvas, ứng dụng dùng nền lyrics bình thường.

### 4.5 Thời tiết (Opt-In)

Khi bạn bật widget thời tiết, V-Notch thực hiện các yêu cầu mạng sau đây **chỉ sau khi** bạn đã chủ động bật tính năng này. Widget thời tiết **tắt theo mặc định**; không có yêu cầu liên quan đến thời tiết nào được thực hiện khi cài đặt mới cho đến khi bạn bật nó.

- **Định vị dựa trên IP (mặc định):** `https://ipwho.is/` — Vị trí gần đúng (vĩ độ, kinh độ, thành phố) của bạn được xác định từ địa chỉ IP. Đây **không phải** vị trí GPS chính xác; nó là một xấp xỉ địa lý thô dựa trên khu vực đã đăng ký của IP. Chỉ endpoint HTTPS được sử dụng.
- **Thành phố thủ công (tùy chọn):** Nếu bạn nhập tên thành phố thủ công, `https://geocoding-api.open-meteo.com/v1/search` được dùng để phân giải thành tọa độ. Khi có thành phố thủ công, không có tra cứu IP nào được thực hiện.
- **Dự báo thời tiết:** `https://api.open-meteo.com/v1/forecast` — Vĩ độ/kinh độ (từ tra cứu IP hoặc nhập thành phố thủ công) được gửi tới Open-Meteo để lấy nhiệt độ hiện tại, mã thời tiết, cao/thấp hàng ngày, và múi giờ.
- **Tần suất:** Mỗi 15 phút khi widget thời tiết đang hoạt động. Các yêu cầu bị hủy khi bạn tắt tính năng.

Nếu ipwho.is không truy cập được, widget thời tiết hiển thị "không khả dụng" và không dùng phương án dự phòng — không có endpoint HTTP nào được sử dụng.

Cả ba endpoint đều là dịch vụ bên thứ ba với chính sách bảo mật riêng:
- [ipwho.is/privacy](https://ipwho.is/privacy)
- [open-meteo.com/privacy](https://open-meteo.com/privacy)

**Dữ liệu gửi đi:** Địa chỉ IP của bạn (tới ipwho.is), hoặc tên thành phố (tới Open-Meteo geocoding), và tọa độ vĩ độ/kinh độ (tới Open-Meteo forecast). Không có dữ liệu cá nhân nào khác được bao gồm.

### 4.6 Bên thứ ba

Các dịch vụ nêu trên (Spotify, GitHub, Google/YouTube, các instance Piped/Invidious, SoundCloud, LRCLIB, ipwho.is, và Open-Meteo) là các bên thứ ba độc lập với chính sách bảo mật riêng của họ. Khi V-Notch liên hệ với họ, địa chỉ IP của bạn tất yếu sẽ hiển thị với dịch vụ đó, như với mọi yêu cầu web thông thường. V-Notch không kiểm soát và không chịu trách nhiệm về cách các dịch vụ đó xử lý yêu cầu. Nếu muốn tránh các tra cứu này, bạn có thể tắt các tính năng ảnh bìa/lời bài hát/Canvas, thời tiết, và kiểm tra cập nhật, hoặc chặn truy cập mạng của ứng dụng.

---

## 5. Lưu trữ dữ liệu cục bộ

Toàn bộ dữ liệu lâu dài do V-Notch tạo ra chỉ nằm trên thiết bị của bạn.

### 5.1 Cài đặt (`%APPDATA%\V-Notch\settings.json`)

 Lưu các tùy chọn của bạn: kích thước và vị trí notch, kiểu hiển thị và animation, thông báo, ngôn ngữ, hành vi khởi động, File Shelf và các cờ tính năng. Tệp có thể chứa YouTube API key nếu bạn tự cung cấp và phiên Spotify nếu bạn chọn Kết nối Spotify. Cả hai đều được mã hóa bằng Windows DPAPI trước khi ghi xuống đĩa, gắn với tài khoản Windows hiện tại và không thể giải mã bởi người dùng hoặc máy khác. Nếu DPAPI không khả dụng, các giá trị nhạy cảm này không được lưu.

### 5.2 Nhật ký chẩn đoán (`vnotch-debug.log`)

Nằm trong thư mục chương trình của ứng dụng, log này ghi lại các sự kiện và lỗi của ứng dụng để giúp chẩn đoán sự cố. Nó được tự động xoay vòng khi đạt khoảng 5 MB. Log dự kiến chỉ chứa thông tin chẩn đoán kỹ thuật (và, để gỡ lỗi media, có thể bao gồm tên bài/URL mà ứng dụng đang xử lý). **Log này không bao giờ được truyền đi đâu** — nó ở lại trên máy của bạn, và bạn có thể xóa bất cứ lúc nào.

### 5.3 Mô hình ONNX tùy chọn

Nếu có, tệp mô hình cắt ảnh thông minh (`yolo11n.onnx`) được lưu cục bộ cùng với ứng dụng và chỉ dùng cho phân tích ảnh trên thiết bị.

Bạn có thể xóa toàn bộ dữ liệu đã lưu bất cứ lúc nào bằng cách xóa thư mục `%APPDATA%\V-Notch\` và thư mục ứng dụng.

---

## 6. Dữ liệu mà V-Notch KHÔNG thu thập

V-Notch **không**:
- thu thập, bán, hoặc chia sẻ thông tin cá nhân với bên thứ ba cho mục đích tiếp thị;
- chạy analytics, telemetry, theo dõi hành vi, hay fingerprinting;
- gửi báo cáo sự cố tự động hay thống kê sử dụng;
- ghi âm thanh, video, hay nội dung màn hình;
- đọc, tải lên, hay sao lưu nội dung tệp của bạn;
- truy cập vị trí GPS chính xác của thiết bị;
- tạo tài khoản, hồ sơ, hay định danh quảng cáo;
- lưu giữ nội dung clipboard.

---

## 7. Bảng tham chiếu quyền

| Quyền / API | Mục đích | Bắt buộc? |
|---|---|---|
| Media Session (SMTC) | Hiển thị phương tiện đang phát | Có (tính năng cốt lõi) |
| Audio Endpoint (Core Audio) | Đọc/điều khiển âm lượng hệ thống | Có (tính năng cốt lõi) |
| Internet | Kiểm tra cập nhật, tra cứu ảnh bìa & lời, thời tiết | Tùy chọn |
| Camera | Xem trước camera trong notch | Opt-in |
| Hệ thống tệp | File Shelf kéo-thả | Opt-in |
| UI Automation | Phát hiện URL media đang phát trong trình duyệt | Dùng cho phát hiện media |
| Bluetooth (liệt kê thiết bị) | Thông báo kết nối/ngắt kết nối | Tùy chọn |
| Trình lắng nghe Clipboard | Hoạt ảnh xác nhận "Copied" | Tùy chọn |

---

## 8. Bảo mật

V-Notch chạy với quyền người dùng tiêu chuẩn và không yêu cầu quyền quản trị viên cho hoạt động bình thường. Việc nâng quyền quản trị chỉ được yêu cầu khi cài đặt bản cập nhật (để chạy bộ cài). Vì ứng dụng hoàn toàn mã nguồn mở, bất kỳ ai cũng có thể kiểm tra chính xác những gì nó làm tại [github.com/rainaku/V-Notch](https://github.com/rainaku/V-Notch).

---

## 9. Quyền riêng tư trẻ em

V-Notch không thu thập dữ liệu cá nhân từ bất kỳ ai, bao gồm trẻ em, và không hướng bất kỳ nội dung nào riêng cho trẻ em. Ứng dụng phù hợp cho mọi lứa tuổi.

---

## 10. Sử dụng quốc tế

V-Notch xử lý dữ liệu cục bộ trên thiết bị của bạn. Dữ liệu duy nhất đi qua mạng là dữ liệu yêu cầu giới hạn được mô tả trong Mục 4, gửi tới các dịch vụ bên thứ ba liệt kê ở đó, vốn có thể hoạt động ở nhiều quốc gia khác nhau. Không có dữ liệu cá nhân nào được nhà phát triển chuyển đi hay lưu trữ.

---

## 11. Thay đổi chính sách

Chính sách bảo mật này có thể được cập nhật khi các tính năng thay đổi. Các thay đổi quan trọng sẽ được phản ánh trong tài liệu này, trong changelog của ứng dụng, và qua ngày hiệu lực cùng số phiên bản được cập nhật ở trên. Việc tiếp tục sử dụng ứng dụng sau khi cập nhật đồng nghĩa với việc chấp nhận chính sách đã sửa đổi.

---

## 12. Liên hệ

Mọi câu hỏi, lo ngại, hoặc yêu cầu liên quan đến dữ liệu có thể được gửi bằng cách tạo issue tại:
[https://github.com/rainaku/V-Notch/issues](https://github.com/rainaku/V-Notch/issues)
