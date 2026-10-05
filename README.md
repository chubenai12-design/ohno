# Garena Orchestrator

Một image, hai chế độ chạy:

- `master`: admin dashboard, quản lý vệ tinh và key ProxyXoay, tự whitelist IP và cấp proxy.
- `satellite`: kết nối tới master, báo IP public của VPS và sử dụng proxy do master cấp.

Repo này là lớp điều phối. Nó **không chạy engine WinForms/WebView2 trên Linux**. Khi satellite chạy Windows, có thể gắn `GarenaRegisterEngine` hiện tại làm executor ở bước tiếp theo.

## Chạy local

Yêu cầu .NET 10 SDK.

PowerShell:

```powershell
$env:ADMIN_PASSWORD = "change-this-password"
$env:MASTER_ENCRYPTION_KEY = "replace-with-at-least-24-random-characters"
$env:PORT = "8080"
dotnet run -- master
```

Mở `http://localhost:8080`, đăng nhập Basic Auth bằng `admin` và mật khẩu trên. Tạo vệ tinh trong dashboard, sao chép token rồi chạy terminal thứ hai:

```powershell
$env:PORT = "8081"
dotnet run -- satellite --master-url http://localhost:8080 --agent-token "TOKEN_VUA_TAO" --name "local-01" --slots 1
```

## Deploy hai Render Web Service

Có thể dùng `render.yaml`, hoặc tạo thủ công hai Web Service cùng trỏ tới repo/Dockerfile này.

### Master

Đặt environment:

```text
MODE=master
```

Không bắt buộc cấu hình Docker Command/Start Command. Nếu Render để trống command, ứng dụng mặc định chạy `master`.

Environment bắt buộc:

| Biến | Ý nghĩa |
|---|---|
| `ADMIN_USER` | Tài khoản admin, mặc định `admin` |
| `ADMIN_PASSWORD` | Mật khẩu dashboard |
| `MASTER_ENCRYPTION_KEY` | Chuỗi ngẫu nhiên tối thiểu 24 ký tự; không thay sau khi đã lưu key |
| `PUBLIC_URL` | URL master, ví dụ `https://garena-master.onrender.com` |
| `TURSO_URL` | URL database Turso |
| `TURSO_TOKEN` | Token Turso |
| `PROXYXOAY_API_URL` | Tùy chọn; mặc định `https://proxyxoay.shop/api/get.php` |

Nếu không đặt Turso, master lưu ở `data/orchestrator-state.json`. Filesystem Render không bền vững, vì vậy production trên Render nên đặt Turso.

Master tự tạo bảng `orchestrator_state`. Không cần chạy migration thủ công.

### Satellite

Đặt environment:

```text
MODE=satellite
```

Environment:

| Biến | Ý nghĩa |
|---|---|
| `MASTER_URL` | Public URL của master |
| `AGENT_TOKEN` | Token tạo từ dashboard master |
| `SATELLITE_NAME` | Tên hiển thị |
| `SATELLITE_SLOTS` | Số slot, hiện dùng để báo capacity |

Satellite mở `/health` trên `$PORT`, vì vậy có thể chạy dưới dạng Render Web Service. Kết nối heartbeat về master luôn đi trực tiếp. Master dùng IP quan sát được từ kết nối này làm `whitelist` khi gọi ProxyXoay.

> **Quan trọng:** Render không đảm bảo một IP outbound duy nhất cho service mặc định. Một service có thể dùng bất kỳ IP nào trong các CIDR của region. Dashboard hiển thị IP đang được quan sát để chẩn đoán; để allowlist ổn định, sao chép toàn bộ dải tại **Render service → Connect → Outbound**, hoặc dùng Dedicated Outbound IP. Xem [Render Outbound IP Addresses](https://render.com/docs/outbound-ip-addresses).

## Cấp proxy tự động bằng key ProxyXoay

1. Mở dashboard master và thêm các key ProxyXoay.
2. Tạo/deploy satellite và chờ trạng thái Online.
3. Master tự lấy một key chưa sử dụng, gắn key đó với satellite.
4. Master gọi `get.php` với `nhamang=random`, `tinhthanh=0` và `whitelist=<IP vệ tinh>`.
5. Proxy HTTP trả về được gửi cho satellite qua heartbeat.
6. Satellite kiểm tra proxy mỗi 60 giây và báo IP proxy, độ trễ hoặc lỗi.
7. Master tự gọi API lại 90 giây trước hạn hoặc ngay khi IP outbound của vệ tinh thay đổi.

Mỗi key có giới hạn cứng tối đa một lần gọi API trong 61 giây. Nếu proxy gần hết hạn, IP thay đổi hoặc API trả lỗi trong khoảng chờ này, master đợi đủ 61 giây rồi mới gọi lại.

Mỗi key chỉ cấp cho một vệ tinh tại một thời điểm. Khi xóa vệ tinh hoặc tắt key, key được trả lại kho để phân cho vệ tinh khác. Key và proxy password được mã hóa AES-GCM bằng `MASTER_ENCRYPTION_KEY`; admin API chỉ trả key đã che ký tự.

## Build Docker

```text
docker build -t garena-orchestrator .
docker run --rm -p 8080:8080 -e PORT=8080 -e ADMIN_PASSWORD=change-me -e MASTER_ENCRYPTION_KEY=replace-with-a-long-random-secret garena-orchestrator master
```
