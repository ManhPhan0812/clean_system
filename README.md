# Dọn dẹp ổ cứng

**v1.0.0** · **by Until You**

Ứng dụng desktop tiếng Việt để quét và dọn **file tạm cũ trên ổ C**, đồng thời thống kê **Other / dữ liệu lập trình**, viết bằng C# / WPF. Chạy tại máy, không gửi dữ liệu lên mạng, không cần cài Node.js, Python hoặc .NET SDK.

## Chạy ứng dụng

Yêu cầu Windows 10/11 và .NET Framework 4.8. Tải `DonDepOCung.exe` tại [GitHub Releases](https://github.com/ManhPhan0812/clean_system/releases/latest) và mở trực tiếp, hoặc mở `dist\DonDepOCung.exe` sau khi build.

1. Chọn thời gian giữ file mới: 1, 7, 14 hoặc 30 ngày (mặc định 7 ngày).
2. Bấm **Quét ổ C**. Quét chỉ đọc thông tin, chưa xóa dữ liệu.
3. Chọn nhóm cần dọn, bấm **Xem danh sách** để xem tối đa 1.000 file lớn nhất.
4. Bấm **Dọn mục đã chọn** và đọc xác nhận. File được xóa **vĩnh viễn**, không qua Thùng rác.
5. Quét lại sau khi dọn để cập nhật. Có thể bấm **Dừng**; những file đã xóa trước khi dừng không được khôi phục.

Ứng dụng chạy với quyền tài khoản hiện tại. File không đủ quyền sẽ được bỏ qua; không tự xin quyền quản trị.

## Phạm vi và cách kiểm tra

Chỉ quét các đường dẫn sau nếu nằm trên ổ C:

- `%LOCALAPPDATA%\Temp` và `%WINDIR%\Temp` (chọn sẵn sau khi quét).
- `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive` và `ReportQueue`.
- `%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive` và `ReportQueue`.

Báo cáo lỗi là nhóm tùy chọn, không chọn sẵn. Xóa nhóm `ReportQueue` có thể làm mất báo cáo lỗi chưa gửi.

Chỉ nhận file có cả ngày tạo và ngày sửa cuối cùng cũ hơn ngưỡng. Trước khi xóa, kiểm tra lại đường dẫn thực tế, mã định danh file, dung lượng và thời gian. Bỏ qua file đang được đọc/ghi, file chỉ đọc, file hệ thống, file offline, hard link, symlink/junction và thư mục có tổ tiên là liên kết. Không đổi quyền hoặc thuộc tính để ép xóa, không xóa thư mục. Việc file tạm đã cũ không đảm bảo ứng dụng tạo ra nó không còn cần nó; xem danh sách và đóng các ứng dụng liên quan trước khi dọn.

Chức năng dọn file tạm không quét/xóa Downloads, Desktop, Documents, Thùng rác, bộ nhớ trình duyệt, Registry, `Windows.old`, `WinSxS`, `SoftwareDistribution`, `pagefile.sys` hoặc `hiberfil.sys`. **Thùng rác** có mục riêng và yêu cầu xác nhận riêng. Nút **Lưu trữ Windows** mở công cụ của Windows để người dùng tự xem thêm các mục. Microsoft mô tả các tùy chọn này tại [Free up drive space in Windows](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/free-up-drive-space-in-windows).

Mỗi nhóm quét giới hạn 100.000 mục để kiểm soát bộ nhớ; giao diện thông báo khi kết quả chỉ là một phần. Cột **Tổng** đếm cả file mới và file chỉ đọc; cột **Có thể dọn** chỉ tính file đủ điều kiện xóa. Dấu `≥` biểu thị kết quả một phần. Lần quét bị dừng không thể dùng để dọn. Dung lượng hiển thị là tổng kích thước logic của file; dung lượng trống thực tế có thể khác vì nén, cấp phát ổ đĩa và hoạt động của Windows.

Nhật ký TSV UTF-8 lưu ở `%LOCALAPPDATA%\CleanSystem\Logs`, gồm đường dẫn và kết quả từng file. Không dọn nếu không tạo/ghi được nhật ký. Nếu bị tắt đột ngột, dòng `ATTEMPT` có thể chưa có kết quả; nhật ký không phải bản sao lưu. Nút **Nhật ký dọn dẹp** mở thư mục này.

## Thùng rác

Mở **Thùng rác** trên sidebar để đọc tổng dung lượng và số mục trên **tất cả ổ đĩa của tài khoản Windows hiện tại**, bao gồm cả ổ C, D và các ổ khác. **Làm mới** cập nhật số liệu. **Mở Thùng rác Windows** mở File Explorer để xem/khôi phục dữ liệu cần giữ trước khi dọn.

Bấm **Dọn thùng rác** và xác nhận **Có / Không** để xóa vĩnh viễn toàn bộ Thùng rác trên **tất cả ổ đĩa** của tài khoản hiện tại; không áp dụng thời gian giữ file. Nút dọn bị tắt nếu chưa đọc được thông tin hoặc Thùng rác đang trống. Ứng dụng đọc lại số lượng/dung lượng sau xác nhận và từ chối dọn nếu số liệu đã thay đổi. Windows xử lý toàn bộ Thùng rác trên tất cả ổ đĩa tại thời điểm gọi API, nên không có snapshot bất biến cho từng file; tránh thêm dữ liệu vào Thùng rác trong lúc dọn.

Dùng API Windows [SHQueryRecycleBinW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryrecyclebinw) và [SHEmptyRecycleBinW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shemptyrecyclebinw), truyền `null` làm đường dẫn gốc để áp dụng cho tất cả ổ đĩa. Nhật ký ghi rõ `scope=all-drives`. Không xóa trực tiếp `$Recycle.Bin`, không nâng quyền. Windows chạy trên luồng STA riêng để giao diện vẫn phản hồi. API dọn Thùng rác không có cơ chế hủy nên **không có nút Dừng** cho thao tác này; đợi hoàn tất trước khi đóng cửa sổ.

Nhật ký `recycle-bin-*.log` lưu phạm vi, số lượng/dung lượng trước và sau, HRESULT và kết quả. Không gọi API xóa nếu chưa ghi được nhật ký. Lỗi API có thể đã xóa một phần dữ liệu; giao diện sẽ báo lỗi và số mục còn lại nếu đọc được. Dung lượng Windows báo cáo không đảm bảo bằng chính xác dung lượng trống tăng thêm. Kiểm thử chỉ gọi API đọc Thùng rác thật; thao tác xóa được kiểm thử bằng API giả.

## Other / lập trình

Mở **Other / lập trình** rồi bấm **Quét Other** để đo các vị trí mặc định của tài khoản hiện tại trên ổ C. Chọn một dòng và bấm **Cách dọn** để xem đường dẫn, giải thích, lệnh tham khảo, mở thư mục hoặc tài liệu chính thức. Nút **Sao chép lệnh** chỉ ghi vào clipboard, không chạy lệnh. Thống kê này không mô phỏng chính xác cách Windows phân loại mục Other và không phải toàn bộ ổ C.

Để quét riêng, bấm **Quét thư mục này** ngay trên dòng tương ứng, ví dụ Go, Maven, FVM hoặc `.codex`. Lượt này chỉ duyệt thư mục của dòng đó; không cần quét cả Other trước. Các dòng chưa quét vẫn có tổng dung lượng `—`. Kết quả và lựa chọn của những dòng đã quét trước được giữ lại; quét lại một dòng sẽ thay kết quả của chính dòng đó, không cộng trùng file. Có thể dừng lượt quét riêng: các file tìm thấy trong lượt bị dừng không được dùng để dọn, còn kết quả hoàn tất của dòng khác vẫn dùng được. **Quét Other** vẫn quét toàn bộ và thay toàn bộ kết quả.

| Nhóm | Ứng dụng xử lý thế nào |
| --- | --- |
| `.gradle\caches` | **Chạy lệnh** xóa toàn bộ thư mục `caches`. **Dọn mục đã chọn** vẫn chỉ xóa cache sinh lại được đủ tuổi. |
| `.gradle\daemon` | **Chạy lệnh** xóa toàn bộ thư mục `daemon` sau khi đã dừng daemon. **Dọn mục đã chọn** chỉ xóa log đủ tuổi. |
| `go\pkg\mod`, `%LOCALAPPDATA%\go-build` | Chạy `go clean -modcache` / `go clean -cache`; ghim biến cache vào đường dẫn của dòng đang chọn. Không xóa `go\src` hoặc `go\bin`. |
| `.m2\repository` | Chạy lựa chọn PowerShell bạn đã cung cấp: xóa toàn bộ nội dung nhưng giữ thư mục gốc. Giữ `.m2\settings.xml` ở bên ngoài. Có thể mất artifact nội bộ chưa publish. |
| `%LOCALAPPDATA%\Pub\Cache` | Chạy `flutter pub cache clean --force` sau xác nhận trong ứng dụng; ghim `PUB_CACHE`. Có thể ảnh hưởng công cụ global như FVM. |
| `fvm` | **Chọn SDK / chạy**: chọn một phiên bản có trong `fvm\versions`, chạy `fvm list` rồi `fvm remove <version>` với `FVM_CACHE_PATH` ghim vào dòng đó. Không có lựa chọn `--all`; chưa hỗ trợ bản fork. |
| `.cache`, `.codex`, `.gemini` | **Chạy lệnh** xóa toàn bộ nội dung, giữ thư mục gốc. Đây là reset dữ liệu, có thể mất cấu hình, xác thực, lịch sử và công việc chưa lưu. |
| `.gitnexus` | **Chạy lệnh** xóa toàn bộ thư mục tài khoản, gồm index và registry. |
| `.vscode\extensions`, `.cursor\extensions` | **Mở VS Code / Mở Cursor** mở trình soạn thảo; nhấn Ctrl+Shift+X và Uninstall từng tiện ích. Không tự xóa extensions. |

Không chọn sẵn nhóm lập trình. Đóng IDE, dừng build và Gradle daemon trước khi dọn. Ứng dụng chặn cả chọn file khi quét và thao tác xóa khi phát hiện tiến trình `java`, `javaw`, `gradle` hoặc `studio64`; vì không xác định chắc tiến trình Java thuộc công cụ nào, một ứng dụng Java không liên quan cũng có thể chặn dọn Gradle. Không tự dừng tiến trình. Cache sinh lại được có thể làm build tiếp theo chậm hơn.

**Chạy lệnh** có ngay trên từng dòng; không cần quét trước. Hộp xác nhận hiển thị lệnh thực tế, thư mục đích và phạm vi xóa. Mọi thao tác cần xác nhận — dọn file tạm, chạy lệnh và dọn Thùng rác — dùng cùng popup **Có / Không**, không yêu cầu gõ tên thư mục hoặc tick checkbox. **Không** là lựa chọn mặc định; Esc hoặc đóng popup cũng hủy thao tác. Chỉ bấm **Có** mới tiếp tục. Các lệnh không áp dụng ngưỡng tuổi file hoặc snapshot của nút **Dọn mục đã chọn**. Xóa `caches` có thể khiến build sau phải tải lại dependency và không build offline được. Dừng daemon của các phiên bản đang dùng, ví dụ `.\gradlew.bat --stop` trong dự án, rồi đóng IDE trước khi chạy lệnh. Reset `.codex` bị chặn khi phát hiện Codex/ChatGPT đang chạy; đóng ứng dụng đó trước khi reset.

Runner chỉ chạy thao tác cố định theo ID và đường dẫn đăng ký sẵn, không chạy nguyên văn chuỗi hướng dẫn hoặc cho nhập lệnh tùy ý. Kiểm tra lại đích, các tổ tiên và toàn bộ cây con; từ chối symlink/junction trước khi xóa. PowerShell kiểm tra lại đích và dùng `-LiteralPath`. Không tự nâng quyền. Chạy không có cửa sổ terminal mới; kết quả/mã thoát được hiển thị trong ứng dụng và lưu `command-*.log` tại `%LOCALAPPDATA%\CleanSystem\Logs`. Nếu chưa ghi được nhật ký, lệnh không được khởi chạy. Đầu ra trên giao diện giới hạn khoảng 48 KB; nhật ký giữ đầu ra đầy đủ.

**Dừng** kết thúc cả cây tiến trình bằng Windows Job Object; phần đã xóa không được khôi phục. Lệnh quá 30 phút cũng bị dừng. Sau mọi lần chạy, kể cả lỗi/hủy, xóa snapshot quét cũ và yêu cầu quét lại. Một lệnh xóa gặp file bị khóa hoặc thiếu quyền có thể đã xóa một phần trước khi báo lỗi. File log bị lỗi khi lệnh đang chạy sẽ khiến runner dừng tác vụ; nhật ký không phải bản sao lưu.

`Remove-Item -Recurse -Force .gitnexus` xóa thư mục tương đối trong vị trí terminal hiện tại. Dòng GitNexus của ứng dụng thống kê `%USERPROFILE%\.gitnexus`; lệnh sao chép nhắm chính xác vị trí đó, xóa cả `indexes`, `registry.json` và dữ liệu khác nếu có. Dừng server/MCP, kiểm tra và sao lưu dữ liệu cần giữ trước khi xóa. Để chỉ xóa index của một dự án, đối chiếu `gitnexus clean` trong đúng dự án theo [mã nguồn chính thức của GitNexus](https://github.com/abhigyanpatwari/GitNexus/blob/main/gitnexus/src/cli/clean.ts); không nhầm thư mục tài khoản với thư mục trong repository.

Các đường dẫn thống kê là **vị trí mặc định**, không tự đọc cấu hình tùy chỉnh như `GRADLE_USER_HOME`, `GOPATH`, Maven `settings.xml` hay `CODEX_HOME`. Với Go/Pub/FVM, runner ghim biến cache vào đường dẫn hiển thị để không vô tình dọn cache tùy chỉnh ở nơi khác; biến môi trường của hệ thống không bị thay đổi. CLI cần được cài và có trong PATH; thiếu CLI sẽ báo lỗi trước khi chạy. Lệnh dùng thư mục làm việc của ứng dụng, không chạy trong repository của bạn. Chuyển màn hình hoặc đổi thời gian giữ file sẽ bỏ kết quả quét cũ.

Tài liệu đối chiếu: [Go module cache](https://go.dev/ref/mod#module-cache), [Gradle directories và cleanup](https://docs.gradle.org/current/userguide/directory_layout.html), [Maven purge](https://maven.apache.org/plugins/maven-dependency-plugin/examples/purging-local-repository.html), [Dart Pub cache](https://dart.dev/tools/pub/cmd/pub-cache), [FVM commands](https://fvm.app/documentation/guides/basic-commands), [OpenAI Docs: Codex config](https://learn.chatgpt.com/docs/config-file/config-basic). Maven purge mặc định sẽ resolve lại dependency; không nên dùng lệnh mặc định với kỳ vọng xóa sạch toàn bộ repository để giảm dung lượng.

## Build và kiểm thử

Trong PowerShell ở thư mục dự án:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Test
```

Script dùng trình biên dịch .NET Framework có sẵn trong Windows. Đầu ra: `dist\DonDepOCung.exe`. XAML nhúng trong `.exe`, không cần mang theo thư mục `src`.

Bản phát hành hiện tại là **v1.0.0**, tác giả **by Until You**, tại `dist\DonDepOCung.exe`. Thư mục `dist` không được đưa vào Git. Nếu `.exe` đang chạy, đóng ứng dụng trước khi build lại vào cùng đường dẫn.

GitHub Actions build và chạy kiểm thử trên Windows khi push tag phiên bản (ví dụ `v1.0.0`). Phiên bản trong EXE phải khớp tag và có nội dung phát hành tại `releases\<tag>.md`. Workflow tải EXE lên bản nháp, kiểm tra dung lượng và SHA-256 trên GitHub rồi mới công khai release. EXE được tải qua Releases; mã nguồn giữ riêng trong repository.

Kiểm thử tạo fixture riêng trong `test-output`, không dọn thư mục tạm/cache thật. Kiểm tra việc quét chỉ đọc, tuổi file, nhận dạng file thay đổi/thay thế, file đang mở, giới hạn đường dẫn, file chỉ đọc, hard link, junction, hủy thao tác, lỗi nhật ký, thống kê file mới/chỉ đọc, tách hai màn hình, chặn xóa mục chỉ thống kê qua nút dọn file, chính sách Gradle, trạng thái Java thay đổi sau khi quét, quét đúng một thư mục, chặn ID ngoài phạm vi, giữ/thay kết quả khi quét lần lượt và hủy quét riêng. Bộ kiểm thử lệnh xác minh xóa fixture đúng đích, giữ thư mục gốc, file hidden, đường dẫn có dấu nháy/khoảng trắng, chặn junction, audit, CLI giả trả lỗi và hủy cả tiến trình con. Giữ fixture để kiểm tra lại; `test-output` không được commit. Smoke test render ba màn hình và popup Có/Không cho các nhóm lệnh, file tạm, Thùng rác; kiểm tra mặc định Không, kết quả của nút Có/Không và không có ô nhập hay checkbox xác nhận. Không chạy thao tác xóa từ các popup kiểm thử.

## Cấu trúc

- `src/Cleaner.cs`: chính sách đường dẫn, quét, xóa bằng Windows file handle, nhật ký.
- `src/Program.cs`: điều khiển giao diện và thao tác bất đồng bộ.
- `src/Commands.cs`: tạo lệnh cố định, kiểm tra đích, chạy CLI/PowerShell, audit và dừng cây tiến trình.
- `src/RecycleBin.cs`: đọc/dọn Thùng rác trên tất cả ổ đĩa qua Shell API, xác nhận phạm vi, audit và luồng STA.
- `src/MainWindow.xaml`: giao diện WPF.
- `tests/CleanerTests.cs`: kiểm thử bằng file và liên kết thật trong fixture.
- `tests/CommandTests.cs`: chạy lệnh xóa trên fixture, CLI giả, kiểm tra quoting, junction, audit, lỗi và hủy tiến trình con; không chạy lệnh dọn thật của Go/Flutter/FVM.
- `tests/RecycleBinTests.cs`: kiểm tra phạm vi, xác nhận cũ, lỗi audit/API và trạng thái trống bằng API giả; chỉ kiểm tra API đọc thật của Windows.
- `Build.ps1`: build không cần SDK, chạy kiểm thử khi có `-Test`.

Cơ chế xóa tham khảo API Microsoft: [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) và [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle).
