# Rà soát mô tả sản phẩm và truy cập nền tảng

Ngày: 03/10/2026. Phạm vi: ba hạng mục được yêu cầu; đây là ghi nhận thay đổi kỹ thuật, không phải kết luận pháp lý hoặc chứng nhận quyền truy cập nền tảng.

| Hạng mục | Thay đổi | Giới hạn còn lại |
| --- | --- | --- |
| Mô tả sản phẩm | Thay mô tả trong `V-Notch.csproj`, hai chỗ trong `Package.appxmanifest` và dòng giới thiệu README bằng “A desktop status and media companion for Windows”. | Chưa kiểm toán toàn bộ quyền đối với thương hiệu, font, biểu tượng và nội dung bên thứ ba. |
| Spotify Canvas | Tắt mặc định cả tính năng và quyền mạng. Hiện nhãn thử nghiệm, thông báo cookie/điểm truy cập không chính thức/rủi ro tài khoản; yêu cầu xác nhận với lựa chọn mặc định là từ chối. | Cơ chế dùng cookie và điểm truy cập không chính thức vẫn tồn tại khi người dùng chủ động bật. Xác nhận không thay thế chấp thuận từ Spotify. |
| YouTube | Bổ sung thông báo trong Cài đặt và điều khoản Anh–Việt: tuân thủ quyền nội dung, điều khoản truy cập; Fair Use được xét từng trường hợp. | YoutubeExplode vẫn là phương thức lấy phụ đề hiện tại. Giấy phép MIT của thư viện và disclaimer không cấp quyền thu thập tự động từ YouTube. |

## Canvas: điều kiện được kiểm tra trong ứng dụng

`NetworkPrivacy` chỉ cho phép Canvas khi đồng thời có xác nhận đúng phiên bản thông báo, bật tính năng, cho phép mạng và không bật chế độ cục bộ. Controller nhận toàn bộ cấu hình, mặc định chưa được bật; cửa sổ đăng nhập và HTTP của Canvas dùng cùng chính sách quyền mạng.

Cấu hình được nâng lên phiên bản 14. Nâng cấp từ cấu hình cũ sẽ tắt Canvas và yêu cầu xác nhận mới. Cờ bật không có xác nhận hợp lệ cũng được đặt lại. Nhập cấu hình luôn xóa xác nhận Canvas; xuất cấu hình không mang theo xác nhận hoặc cookie. Cấu hình đã xác nhận trên máy được giữ qua việc lưu và tải lại thông thường.

Hai công tắc Canvas trong Cài đặt dùng chung luồng xác nhận. Tắt một trong hai thu hồi xác nhận, dừng tra cứu/hiển thị Canvas và hủy yêu cầu mạng do ứng dụng quản lý. Bật lại yêu cầu xác nhận mới. Tắt tính năng giữ cookie đã mã hóa trên máy; nút Ngắt kết nối xóa cookie đó. Hủy phiên Spotify cần thực hiện qua tài khoản Spotify.

Thông báo Canvas và YouTube được cập nhật ở cả 17 ngôn ngữ. Điều khoản, chính sách quyền riêng tư, README và thông báo thành phần được cập nhật tương ứng.

## Kiểm chứng

- Build Release với `TreatWarningsAsErrors=true` thành công, không cảnh báo. Cả 17 locale qua kiểm tra 795 key và placeholder. Kiểm tra định dạng cho nguồn ứng dụng/tests được sửa và `git diff --check` đều qua.
- Bộ kiểm thử tập trung cuối cùng: **79/79 qua, không bỏ qua**, tại `artifacts/restored-colors-verified-test-results/tests.trx`. Bao gồm cấu hình mới/cũ/nhập từ tệp, từ chối xác nhận, bật rồi thu hồi, chặn trước lớp vận chuyển HTTP, hủy yêu cầu đang chạy, bỏ qua kết quả đến muộn và áp dụng quyền ngay trước đăng nhập. Cũng kiểm tra việc khôi phục màu media theo yêu cầu tiếp theo.
- Lượt full suite với coverage cuối của phần truy cập nền tảng: **1.706/1.707 qua** tại `artifacts/platform-verified-test-results/tests.trx`; `LiquidGlass_LiveDesktop_CaptureCoordinatesAndRapidReopenStayValid` thất bại ở kiểm tra pixel desktop khi mở lại nhanh. Các kiểm thử xác nhận/quyền Canvas qua. Không coi lượt này là toàn bộ suite xanh. Lượt full trước thay đổi áp dụng xác nhận tức thời đã qua 1.707/1.707 (`artifacts/platform-final-test-results/tests.trx`).
- Coverage ứng dụng của lượt full cuối: **43,59% (25.214/57.846 dòng)**, còn dưới cổng phát hành 80%; cổng không bị hạ hoặc bỏ qua.

## Nguồn chính thức và việc còn cần xác minh

- [Apple: Guidelines for Using Apple Trademarks and Copyrights](https://www.apple.com/legal/intellectual-property/guidelinesfor3rdparties.html): tham chiếu để tránh mô tả gây nhầm lẫn về sản phẩm và sự liên kết.
- [Spotify User Guidelines](https://www.spotify.com/us/legal/user-guidelines/): hạn chế scraping và thu thập tự động; có thể đình chỉ quyền truy cập hoặc tài khoản khi vi phạm.
- [YouTube Terms of Service](https://www.youtube.com/static?template=terms): hạn chế truy cập tự động ngoài các ngoại lệ nêu trong điều khoản.
- [U.S. Copyright Office: Fair Use Index](https://www.copyright.gov/fair-use/): đánh giá theo từng trường hợp, không có công thức tự động bảo đảm ngoại lệ.

Mức CRITICAL/HIGH/MEDIUM trong bảng yêu cầu không được xem là kết luận pháp lý đã xác minh. Việc thay mô tả, thêm xác nhận và disclaimer không chứng minh các đường truy cập hiện tại được nền tảng cho phép. Trước khi phân phối đường truy cập chưa có cơ sở cho phép, cần xác minh quyền truy cập hoặc thay thế/tắt đường đó; yêu cầu hiện tại chưa bao gồm việc chuyển sang một API được nền tảng cấp phép.
