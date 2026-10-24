# Đánh giá giấy phép thành phần tray

Ngày kiểm tra: 23/10/2026. Trạng thái: mã hiện tại đã dùng
`Hardcodet.NotifyIcon.Wpf 2.0.1`, thay cho bản 1.1.0 có trong báo cáo cũ.

## Bằng chứng của phiên bản đang dùng

- `V-Notch.csproj` và lockfile của ứng dụng/test chọn 2.0.1.
- Tệp `.nuspec` trong đúng gói NuGet 2.0.1 khai báo MIT và source commit
  `0a9d1a92914db8fb6b1cca1f073469644cd9bf3b`.
- Tệp `LICENSE` trong chính gói đã khôi phục là MIT, khớp với
  [LICENSE ở source commit của gói](https://github.com/hardcodet/wpf-notifyicon/blob/0a9d1a92914db8fb6b1cca1f073469644cd9bf3b/LICENSE).
- Bản nguyên văn từ gói được giữ ở
  [licenses/Hardcodet.NotifyIcon.Wpf-MIT.txt](licenses/Hardcodet.NotifyIcon.Wpf-MIT.txt)
  và sao chép cùng đầu ra build/publish.

Không cần viết lại tray để giải quyết phát hiện CPOL của phiên bản 1.1.0.
Các bản phát hành cũ từng chứa 1.1.0 vẫn phải được đánh giá riêng; nâng cấp hiện
tại không thay đổi giấy phép của những bản đó. Các thành phần khác của bản phát
hành vẫn giữ giấy phép riêng.

## Kiểm tra phát hành

Đối chiếu lockfile và thành phần thực tế trong gói publish/installer; giữ nguyên
thông báo MIT cùng phân phối. Kiểm tra tray trên Windows: thêm/xóa icon, menu,
đổi ngôn ngữ, thao tác bàn phím, DPI và Explorer khởi động lại. Việc cập nhật tài
liệu giấy phép không tự xác nhận các hành vi desktop này đã được thử thủ công.
