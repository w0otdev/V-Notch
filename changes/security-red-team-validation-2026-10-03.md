# Thẩm định báo cáo bảo mật V-Notch — 03/10/2026

Đã đối chiếu báo cáo với mã nguồn và sửa các điểm có thể khắc phục trực tiếp. Các mức CRITICAL/HIGH và điểm CVSS trong báo cáo gốc chưa được xác lập bằng vector, điều kiện khai thác và bằng chứng chạy thực tế; không dùng chúng làm kết luận đã xác nhận.

| Mục | Kết luận từ mã nguồn | Xử lý |
| --- | --- | --- |
| 1. Thực thi UninstallString từ HKCU/HKLM | Có luồng thực thi dữ liệu registry không tin cậy. NSIS khai báo `RequestExecutionLevel user`; chạy bình thường không tự nâng quyền. Nếu người dùng chạy installer với quyền admin, payload có thể kế thừa quyền đó, nhưng không tự trở thành SYSTEM. | Bỏ hoàn toàn việc tự chạy uninstaller cũ từ registry. Người dùng có thể gỡ bản Inno cũ qua Windows Settings. |
| 2. Domain tải xuống | Xác nhận hậu tố thiếu ranh giới domain, tiền tố bucket S3 quá rộng, phạm vi repo quá rộng và ngoại lệ domain test trong mã production. Vượt bộ lọc này chưa đủ để giả mạo update: updater đã kiểm tra manifest ECDSA bằng khóa ghim và hash installer. | Giới hạn origin của installer/manifest vào đường dẫn release của `rainaku/V-Notch`; chỉ cho redirect sang hostname CDN GitHub được liệt kê chính xác. Chặn URL lỗi, HTTP, thông tin đăng nhập trong URL và cổng khác 443. Xóa ngoại lệ localhost/domain test khỏi chính sách production. |
| 3. Cleanup qua batch | Sinh batch bằng đường dẫn là thiết kế cần thay. Tuy nhiên CR/LF và pipe không phải ký tự hợp lệ trong tên file Windows thông thường; kiểm tra `File.Exists` trước đó cũng làm kịch bản mẫu không tự chứng minh được khả năng khai thác. Việc xóa `&`/`%` còn làm thay đổi các đường dẫn hợp lệ. | Thay cả hai batch bằng helper PowerShell có mã cố định, truyền đường dẫn qua environment. Kiểm tra đường dẫn tuyệt đối cục bộ, chặn root, ký tự không hợp lệ và các ancestor là reparse point. Chờ tiến trình sở hữu thoát rồi mới xóa. Uninstaller chọn thư mục chứa chính nó, không đọc registry để chọn đích xóa đệ quy. |
| 4. Restart qua cmd.exe | Xác nhận nội suy đường dẫn vào shell và phụ thuộc mở rộng biến môi trường. Chưa gán mức HIGH cho mọi đường dẫn có `%` hoặc `^` mà thiếu điều kiện khai thác cụ thể. | Chạy trực tiếp executable bằng `ProcessStartInfo.ArgumentList` với `--restart`; dùng cơ chế chờ mutex sẵn có. Nếu khởi chạy thất bại, giữ tiến trình hiện tại. |
| 5. UNC trong Spotlight | Đường dẫn UNC có thể kích hoạt SMB và xác thực Windows, tùy chính sách hệ thống và mạng. Đó có thể là challenge-response NetNTLM; không đồng nghĩa với việc luôn lộ mật khẩu plaintext. | Chặn UNC trước các kiểm tra launch/reveal/elevate, tải icon, lọc kết quả filesystem và phân giải app target. |
| 6. Mutex tĩnh | Có khả năng cản khởi động bởi tiến trình cục bộ. Thêm SID giúp tách người dùng, nhưng không phải bảo vệ hoàn chỉnh trước tiến trình độc hại cùng tài khoản. | Dùng tên mutex trong namespace `Local` kèm SID. Ghi nhận giới hạn same-user còn tồn tại. |
| 7. WebView Spotify | Thiếu chính sách top-level navigation và cửa sổ mới. Điều này là rủi ro phishing/giới hạn trình duyệt nhúng, không tự chứng minh open redirect trên server Spotify hay native RCE. DNS poisoning riêng lẻ không bỏ qua xác thực chứng thư HTTPS. | Chỉ cho HTTPS trên `spotify.com` và subdomain đúng ranh giới; xử lý cửa sổ mới trong WebView, hủy download, tắt host objects và web messages không dùng. Không ghi URL đăng nhập vào log vì có thể chứa token. |
| 8. Repo TOTP bên thứ ba | Là phụ thuộc về khả dụng và độ tin cậy của dữ liệu. JSON được dùng để tạo TOTP, không có bằng chứng thực thi mã từ dữ liệu này. Mã đã giữ cấu hình trong bộ nhớ khi refresh thất bại. | Giữ nguyên cơ chế hiện tại. Chưa có mirror thuộc dự án hay fallback đã được kiểm chứng; không thay URL bằng một nguồn chưa tồn tại. |
| 9. Runtime tải về không xác minh | Xác nhận thiếu xác minh trước khi chạy. Installer còn kiểm tra .NET 10 trong khi app và uninstaller nhắm .NET 8. | Ghim runtime Desktop x64 8.0.29 và SHA-512 từ metadata Microsoft; bắt buộc Authenticode `Valid` với tổ chức signer `Microsoft Corporation`. Chỉ chạy sau khi cả hai kiểm tra đạt. Dùng binary curl/PowerShell từ thư mục hệ thống, không tìm qua PATH. |
| 10. DPAPI không entropy | Đây là giới hạn của DPAPI CurrentUser, không tự là lỗ hổng phân tách giữa ứng dụng. Tiến trình cùng user có thể đọc entropy nếu lưu cùng tài khoản hoặc lấy từ binary; thêm entropy như vậy không tạo ranh giới bảo mật mới. | Giữ định dạng DPAPI để không làm mất khả năng đọc token đã lưu. Không tuyên bố rằng entropy chống được malware cùng user. |

Mục WM_COPYDATA trong bản đồ tấn công chưa có kịch bản chứng minh chi tiết. Everything IPC đang đối chiếu reply ID và giới hạn số item/đọc trong phạm vi buffer, nhưng không xác thực danh tính sender; reply ID không phải chứng thực. Không đánh dấu rủi ro giả mạo IPC đã được giải quyết. Bản vá UNC giúp giảm một đường dẫn gây truy cập mạng từ kết quả không tin cậy. Cần thiết kế riêng nếu yêu cầu xác thực IPC giữa các tiến trình cùng tài khoản.

Giới hạn tương thích và vận hành:

- Điều hướng đăng nhập Google/Apple/Facebook bên ngoài domain Spotify hiện bị hủy. Luồng đăng nhập trực tiếp Spotify vẫn nằm trong allowlist; chưa kiểm thử đăng nhập tương tác bằng tài khoản thật.
- Windows có thể cấu hình Start Menu trên share mạng; việc index các thư mục hệ thống được cấu hình như vậy vẫn có thể dùng SMB. Shortcut hoặc drive mapping cũng có thể dẫn tới nguồn mạng dù chuỗi ban đầu là đường dẫn cục bộ. Bản vá không được xem là chính sách cấm mọi kết nối SMB của Windows.
- Helper cleanup phụ thuộc Windows PowerShell. Nếu PowerShell bị chính sách máy chặn, cần xóa thư mục cài đặt còn lại thủ công.
- Kiểm tra reparse point giảm rủi ro xóa qua junction; không tạo bảo đảm chống mọi race đổi filesystem bởi malware cùng user.
- Khi cập nhật runtime ghim, phải thay URL và digest cùng nhau bằng thông tin từ metadata Microsoft.

Kiểm chứng:

- Trước bản vá: 102 test liên quan hiện có đạt.
- Sau bản vá: 143 test tập trung đạt, gồm domain giả mạo, redirect HTTPS không tin cậy, chữ ký manifest, UNC, chính sách điều hướng và cleanup, kể cả UNC dùng dấu phân cách trộn `\/` hoặc `/\`.
- Cleanup integration dùng thư mục tạm riêng có dấu nháy đơn, `&`, `%`, `^`, `!` và tiếng Việt; xác nhận chờ owner thoát, chỉ xóa đích và giữ file sentinel bên cạnh. Test junction không cần quyền tạo symbolic link.
- `Uninstall.csproj` build thành công với 0 warning/0 error.
- Hai biến thể NSIS biên dịch thành công với payload fixture, không chạy installer. Biến thể self-contained có warning expected: hàm kiểm tra runtime không được gọi.
- Tải runtime Microsoft thật để kiểm tra, không thực thi: file chính thức đạt hash và chữ ký; fixture không ký bị từ chối dù hash khớp; runtime ký hợp lệ bị từ chối khi digest sai.
- Toàn bộ suite lần cuối: 1.529 đạt / 4 lỗi trên 1.533 test. Ba lỗi ảnh bìa do thiếu `dark-textured-artwork.png`; một lỗi animation replay. Lần chạy toàn suite trước đó trên bản vá có 5 lỗi, gồm cả hai test animation replay. Bản HEAD chưa sửa, với cùng fixture hiện có: 1.476 đạt / đúng 5 lỗi tương ứng; hai test animation đều đạt khi chạy riêng. Đây là nhóm test animation không ổn định khi chạy toàn suite, đã tái hiện trước bản vá. Bản vá bổ sung 52 trường hợp test và không tạo lỗi test mới quan sát được.

Nguồn đối chiếu:

- [Microsoft: UAC và quyền người dùng](https://learn.microsoft.com/en-us/windows/win32/secauthz/user-account-control).
- [Microsoft: phạm vi bảo vệ và entropy của DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).
- [Microsoft: hướng dẫn bảo mật WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security).
- [Microsoft: ownership và tính reentrant của mutex](https://learn.microsoft.com/en-us/windows/win32/sync/mutex-objects).
- [Metadata phát hành .NET 8 chính thức](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json), đối chiếu trực tiếp URL và SHA-512 của Windows Desktop Runtime 8.0.29 x64.
- [voidtools: kiểm tra Everything query reply](https://www.voidtools.com/en-us/support/everything/sdk/everything_isqueryreply/).
