# Dọn dẹp ổ cứng

**v1.0.0** · **by Until You**

Ứng dụng desktop tiếng Việt để quét và dọn **file tạm trên ổ C**, đồng thời thống kê **Other / dữ liệu lập trình**, viết bằng C# / WPF. Chạy tại máy, không gửi dữ liệu lên mạng, không cần cài Node.js, Python hoặc .NET SDK.

## Chạy ứng dụng

Yêu cầu Windows 10/11 và .NET Framework 4.8. Tải `DonDepOCung.exe` tại [GitHub Releases](https://github.com/ManhPhan0812/clean_system/releases/latest) và mở trực tiếp, hoặc mở `dist\DonDepOCung.exe` sau khi build.

Nút **Làm mới** cạnh **Dung lượng trống · C:** có trên cả **Dọn file tạm** và **Other / lập trình** để cập nhật dung lượng trống, đã dùng và thanh dung lượng ổ C. Thao tác này giữ kết quả quét và các mục đã chọn; không quét lại thư mục.

1. Mặc định **Không giới hạn ngày**: xét cả file mới tạo trong Temp người dùng và Temp Windows. Có thể chọn 1, 7, 14 hoặc 30 ngày để giữ lại file mới hơn ngưỡng.
2. Bấm **Quét ổ C**. Quét chỉ đọc thông tin, chưa xóa dữ liệu.
3. Chọn nhóm cần dọn, bấm **Xem danh sách** để xem tối đa 1.000 file lớn nhất.
4. Bấm **Dọn mục đã chọn** và đọc xác nhận. File được xóa **vĩnh viễn**, không qua Thùng rác.
5. Quét lại sau khi dọn để cập nhật. Có thể bấm **Dừng**; những file đã xóa trước khi dừng không được khôi phục.

Ứng dụng khởi động bằng quyền tài khoản hiện tại. Khi quét có chọn **File tạm Windows** mà chưa có quyền quản trị, ứng dụng hỏi **Có / Không**: chọn **Có** để Windows hiện UAC và mở lại chính EXE bằng quyền quản trị; chọn **Không** hoặc từ chối UAC để tiếp tục bằng quyền hiện tại, bỏ qua mục thiếu quyền. Có thể bấm **Cấp quyền Windows Temp** để thử lại. Bản quản trị giữ lựa chọn tuổi file, chỉ chọn nhóm Windows Temp và yêu cầu quét lại rồi xác nhận dọn; không chuyển danh sách file từ bản cũ và không tự xóa sau UAC. Nếu đã chạy bằng admin, ứng dụng không hỏi cấp quyền lần nữa. Những file bị khóa hoặc được bảo vệ vẫn có thể bị bỏ qua dù có quyền admin.

## Phạm vi và cách kiểm tra

Chỉ quét các đường dẫn sau nếu nằm trên ổ C:

- `%LOCALAPPDATA%\Temp` và `%WINDIR%\Temp` (chọn sẵn sau khi quét).
- `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive` và `ReportQueue`.
- `%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive` và `ReportQueue`.

Báo cáo lỗi là nhóm tùy chọn, không chọn sẵn. Xóa nhóm `ReportQueue` có thể làm mất báo cáo lỗi chưa gửi.

Ở chế độ **Không giới hạn ngày**, không loại file vì ngày tạo hoặc ngày sửa. Nếu chọn mức 1/7/14/30 ngày, chỉ nhận file có cả ngày tạo và ngày sửa cuối cùng cũ hơn ngưỡng; dòng **Giữ do tuổi file** cho biết số file và dung lượng bị loại vì điều kiện này. Trước khi xóa, kiểm tra lại đường dẫn thực tế, mã định danh file, dung lượng và thời gian. Bỏ qua file đang được đọc/ghi, file chỉ đọc, file hệ thống, file offline, hard link, symlink/junction và thư mục có tổ tiên là liên kết. Không đổi quyền hoặc thuộc tính để ép xóa, không xóa thư mục. Xem danh sách và đóng các ứng dụng, trình cài đặt liên quan trước khi dọn; file tạm mới tạo có thể còn cần dùng dù hiện chưa bị khóa.

Chức năng dọn file tạm không quét/xóa Downloads, Desktop, Documents, Thùng rác, bộ nhớ trình duyệt, Registry, `Windows.old`, `WinSxS`, `SoftwareDistribution`, `pagefile.sys` hoặc `hiberfil.sys`. **Thùng rác** có mục riêng và yêu cầu xác nhận riêng. **Windows Update · Download** trong Other có thao tác riêng để dọn cache cập nhật. Nút **Lưu trữ Windows** mở công cụ của Windows để người dùng tự xem thêm các mục. Microsoft mô tả các tùy chọn này tại [Free up drive space in Windows](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/free-up-drive-space-in-windows).

Mỗi nhóm quét giới hạn 100.000 mục để kiểm soát bộ nhớ; giao diện thông báo khi kết quả chỉ là một phần. Cột **Tổng** đếm cả file mới và file chỉ đọc; cột **Có thể dọn** chỉ tính file đủ điều kiện xóa. Dấu `≥` biểu thị kết quả một phần. Lần quét bị dừng không thể dùng để dọn. Dung lượng hiển thị là tổng kích thước logic của file; dung lượng trống thực tế có thể khác vì nén, cấp phát ổ đĩa và hoạt động của Windows.

Nhật ký TSV UTF-8 lưu ở `%LOCALAPPDATA%\CleanSystem\Logs`, gồm đường dẫn và kết quả từng file. Không dọn nếu không tạo/ghi được nhật ký. Nếu bị tắt đột ngột, dòng `ATTEMPT` có thể chưa có kết quả; nhật ký không phải bản sao lưu. Nút **Nhật ký dọn dẹp** mở thư mục này.

## Thùng rác

Mở **Thùng rác** trên sidebar để đọc tổng dung lượng và số mục trên **tất cả ổ đĩa của tài khoản Windows hiện tại**, bao gồm cả ổ C, D và các ổ khác. **Làm mới** cập nhật số liệu. **Mở Thùng rác Windows** mở File Explorer để xem/khôi phục dữ liệu cần giữ trước khi dọn.

Bấm **Dọn thùng rác** và xác nhận **Có / Không** để xóa vĩnh viễn các mục trong Thùng rác trên **tất cả ổ đĩa** của tài khoản hiện tại; không áp dụng thời gian giữ file. Nút dọn bị tắt nếu chưa đọc được thông tin hoặc Thùng rác đang trống. Ứng dụng đọc lại số lượng/dung lượng sau xác nhận và từ chối dọn nếu số liệu đã thay đổi, rồi lấy danh sách Shell trước khi xóa từng mục. Các mục thêm vào sau khi lấy danh sách không được dọn trong lượt này.

Dùng [SHQueryRecycleBinW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryrecyclebinw) với gốc `null` để đo tất cả ổ đĩa, và [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation) trên từng mục của namespace Thùng rác. Không xóa trực tiếp `$Recycle.Bin`. Khi cần quyền xóa, popup **Có / Không** xuất hiện: **Có** cho phép Windows thử lại riêng mục đó và hỏi quyền quản trị nếu cần; **Không**, Esc hoặc đóng popup giữ mục đó và tiếp tục các mục còn lại. Từ chối UAC cũng chỉ bỏ qua mục hiện tại. File bị khóa/không xóa được được giữ lại, không lặp thử vô hạn. Luồng STA có vòng xử lý thông điệp cho Shell/COM; giao diện cập nhật số mục đã xóa/bỏ qua, tự tắt tiến trình và báo kết quả khi kết thúc, kể cả khi còn dữ liệu. **Không có nút Dừng** trong thao tác Shell; đợi hoàn tất trước khi đóng cửa sổ.

Nhật ký `recycle-bin-*.log` lưu phạm vi `scope=all-drives`, từng lần thử/xóa/bỏ qua và lựa chọn quyền, số lượng/dung lượng trước và sau. Không gọi API xóa nếu chưa ghi được nhật ký. Một thư mục có thể đã xóa một phần nội dung trước khi gặp mục không xóa được; số mục đã xóa chỉ tính các mục Shell hoàn tất, còn số mục bỏ qua bao gồm thư mục chưa xóa hết. Dung lượng Windows báo cáo không đảm bảo bằng chính xác dung lượng trống tăng thêm. Kiểm thử xóa dùng API giả hoặc file fixture riêng; Thùng rác thật chỉ được đọc.

## Other / lập trình

Mở **Other / lập trình** rồi bấm **Quét Other** để đo các vị trí mặc định của tài khoản hiện tại trên ổ C. Đường dẫn được ghép theo tài khoản đang đăng nhập; nếu công cụ có biến môi trường cache riêng (xem bên dưới) thì dùng giá trị đó thay cho đường mặc định. Chọn một dòng và bấm **Cách dọn** để xem đường dẫn, giải thích, lệnh tham khảo, mở thư mục hoặc tài liệu chính thức. Nút **Sao chép lệnh** chỉ ghi vào clipboard, không chạy lệnh. Thống kê này không mô phỏng chính xác cách Windows phân loại mục Other và không phải toàn bộ ổ C.

Mỗi dòng tự cho biết trạng thái trên máy này: có dữ liệu, chưa có thư mục, hay chưa cài công cụ. Tùy chọn **Chỉ hiện mục có trên máy** ẩn những dòng vừa không có thư mục vừa không có công cụ trong PATH; bỏ chọn để xem đủ danh sách. Nút **Chạy lệnh** bị tắt khi thiếu CLI tương ứng, và tooltip nói rõ cần cài gì. Trạng thái được đọc lại mỗi lần mở màn hình hoặc đổi bộ lọc, nên cài công cụ xong không cần khởi động lại ứng dụng.

Để quét riêng, bấm **Quét thư mục này** ngay trên dòng tương ứng, ví dụ Go, Maven, FVM hoặc `.codex`. Lượt này chỉ duyệt thư mục của dòng đó; không cần quét cả Other trước. Các dòng chưa quét vẫn có tổng dung lượng `—`. Kết quả và lựa chọn của những dòng đã quét trước được giữ lại; quét lại một dòng sẽ thay kết quả của chính dòng đó, không cộng trùng file. Có thể dừng lượt quét riêng: các file tìm thấy trong lượt bị dừng không được dùng để dọn, còn kết quả hoàn tất của dòng khác vẫn dùng được. **Quét Other** vẫn quét toàn bộ và thay toàn bộ kết quả.

| Nhóm | Ứng dụng xử lý thế nào |
| --- | --- |
| `%WINDIR%\SoftwareDistribution\Download` | **Dọn cache cập nhật**: xác nhận Có/Không, dừng `wuauserv` / `bits`, xóa nội dung `Download`, rồi khởi động lại cả hai dịch vụ. Cần mở ứng dụng bằng **Run as administrator**. |
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

Runner chỉ chạy thao tác cố định theo ID và đường dẫn đăng ký sẵn, không chạy nguyên văn chuỗi hướng dẫn hoặc cho nhập lệnh tùy ý. Kiểm tra lại đích, các tổ tiên và toàn bộ cây con; từ chối symlink/junction trước khi xóa. PowerShell kiểm tra lại đích và dùng đường dẫn nguyên văn. Các thao tác xóa thư mục/nội dung trong Other xử lý từng file/thư mục: file bị khóa hoặc thiếu quyền được giữ lại, ghi `SKIPPED` và tiếp tục các mục khác; liên kết xuất hiện sau kiểm tra vẫn khiến thao tác dừng. Kết quả hiển thị tổng đã xóa/bỏ qua, kể cả khi đầu ra chi tiết bị giới hạn. Không tự nâng quyền các lệnh Other. Chạy không có cửa sổ terminal mới; kết quả/mã thoát được hiển thị trong ứng dụng và lưu `command-*.log` tại `%LOCALAPPDATA%\CleanSystem\Logs`. Nếu chưa ghi được nhật ký, lệnh không được khởi chạy. Đầu ra trên giao diện giới hạn khoảng 48 KB; nhật ký giữ đầu ra đầy đủ.

**Dừng** có thể hủy bước kiểm tra thư mục trước khi chạy lệnh; bước này có tiến độ và giới hạn 60 giây. Nếu lỗi/hủy, nhật ký ghi `END precheck` và lệnh chưa được chạy. Với lệnh thông thường, Dừng kết thúc cả cây tiến trình bằng Windows Job Object; phần đã xóa không được khôi phục. Lệnh thông thường quá 30 phút cũng bị dừng. **Ngoại lệ: Windows Update** ẩn nút Dừng khi bắt đầu chạy lệnh để bước khởi động lại dịch vụ có thể hoàn tất. Sau mọi lần chạy, kể cả lỗi/hủy, xóa snapshot quét cũ và yêu cầu quét lại. Một lệnh xóa gặp file bị khóa hoặc thiếu quyền có thể đã xóa một phần trước khi báo lỗi. File log bị lỗi khi lệnh thông thường đang chạy sẽ khiến runner dừng tác vụ; với Windows Update, runner chờ bước khởi động lại dịch vụ rồi báo lỗi nhật ký. Nhật ký không phải bản sao lưu.

### Cache Windows Update

Trong **Other / lập trình**, chọn **Windows Update · Download**. **Quét thư mục này** chỉ đo dung lượng, không dừng dịch vụ. **Dọn cache cập nhật** hiển thị đường dẫn và lưu ý sau trong popup Có/Không:

> Không thực hiện khi Windows đang tải hoặc cài đặt bản cập nhật, hay đang yêu cầu khởi động lại để hoàn tất cập nhật. Nếu có lỗi file đang được sử dụng, không nên cố xóa.

Mục này cần quyền quản trị: đóng ứng dụng rồi nhấp phải `DonDepOCung.exe` → **Run as administrator**. Ứng dụng vẫn khởi động với quyền hiện tại và không tự nâng quyền. Khi chưa chạy bằng quyền admin, nhấp/chọn dòng Windows Update sẽ hiện cảnh báo ngay và không cho truy cập. Quét riêng, Cách dọn và chạy lệnh đều bị chặn trước khi mở chức năng; **Quét Other** bỏ qua mục này, các mục khác vẫn dùng bình thường.

Trước khi dừng dịch vụ, đọc trạng thái Windows Update qua [RebootRequired](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-isysteminformation-get_rebootrequired) và [IsBusy](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-get_isbusy). Nếu yêu cầu khởi động lại, đang cài/gỡ cập nhật, không đọc được trạng thái hoặc dịch vụ bị Disabled/đang chuyển trạng thái, không dọn. Các kiểm tra này không xác định mọi hoạt động tải cập nhật và không khóa hoạt động cập nhật khác; người dùng vẫn cần kiểm tra Windows Update trước khi xác nhận.

Chỉ xử lý `%WINDIR%\SoftwareDistribution\Download` khi Windows nằm trên ổ C, giữ thư mục gốc và `DataStore`. Kiểm tra lại đích, tổ tiên và các đường dẫn liên kết trước khi dừng dịch vụ, rồi kiểm tra lại sau khi hai dịch vụ đã dừng. Xóa gặp lỗi sẽ dừng ngay, không thử lại, không chiếm quyền sở hữu/đổi ACL hay ép giải phóng file đang dùng. Các gói cập nhật có thể cần tải lại.

Ứng dụng hỗ trợ đường dẫn dài trong bước quét/kiểm tra và dọn cache Windows Update qua API file với tiền tố đường dẫn mở rộng, không thay đổi Registry hay cấu hình Windows. Dọn nội dung có tác dụng tương đương `Remove-Item -Recurse -Force`: xử lý file chỉ đọc nhưng không giải phóng file bị khóa. Số mục đã kiểm tra/xóa được cập nhật trong tiến độ và nhật ký. Mỗi lượt kiểm tra lại đường dẫn có giới hạn 60 giây; bước xóa giới hạn 5 phút và sẽ chuyển sang khởi động lại dịch vụ nếu hết thời gian. Giới hạn được kiểm tra giữa các thao tác file.

Khởi động lại `wuauserv` và `bits` trong `finally`, kể cả khi một lệnh dừng hoặc xóa bị lỗi; lỗi khởi động một dịch vụ không ngăn thử khởi động dịch vụ còn lại. Dừng bằng `Stop-Service -NoWait` rồi đợi mỗi dịch vụ đạt trạng thái mong muốn tối đa 30 giây sau lệnh điều khiển. Lỗi khởi động lại được báo trong kết quả và nhật ký. Có thể bấm Dừng khi đang kiểm tra trước khi chạy lệnh. Khi lệnh đã bắt đầu, nút Dừng bị ẩn và đóng cửa sổ sẽ đợi hoàn tất. Worker cũng không bị kết thúc do Job Object của giao diện bị đóng, để có thể hoàn thành bước khởi động lại dịch vụ. Nếu máy bị tắt hoặc worker bị kết thúc cưỡng bức, vẫn cần kiểm tra lại dịch vụ Windows Update.

Kiểm thử dùng thư mục fixture, dịch vụ và trạng thái cập nhật giả; không dừng/khởi động dịch vụ hoặc dọn cache Windows Update thật. Đối chiếu thao tác dịch vụ tại [tài liệu Microsoft](https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update).

### Đường dẫn và công cụ lập trình

`Remove-Item -Recurse -Force .gitnexus` xóa thư mục tương đối trong vị trí terminal hiện tại. Dòng GitNexus của ứng dụng thống kê `%USERPROFILE%\.gitnexus`; lệnh sao chép nhắm chính xác vị trí đó, xóa cả `indexes`, `registry.json` và dữ liệu khác nếu có. Dừng server/MCP, kiểm tra và sao lưu dữ liệu cần giữ trước khi xóa. Để chỉ xóa index của một dự án, đối chiếu `gitnexus clean` trong đúng dự án theo [mã nguồn chính thức của GitNexus](https://github.com/abhigyanpatwari/GitNexus/blob/main/gitnexus/src/cli/clean.ts); không nhầm thư mục tài khoản với thư mục trong repository.

Các đường dẫn thống kê là **vị trí mặc định** của tài khoản hiện tại. Ứng dụng đọc thêm biến môi trường cache do chính công cụ công bố — `GRADLE_USER_HOME`, `GOMODCACHE`, `GOCACHE`, `PUB_CACHE`, `FVM_CACHE_PATH`, `CODEX_HOME` — và dùng giá trị đó thay cho đường mặc định; biến không đặt, không phải đường dẫn tuyệt đối hoặc không dùng được thì giữ mặc định. Ứng dụng vẫn không đọc `GOPATH` khi khác `GOMODCACHE`, Maven `settings.xml` hay cấu hình riêng khác, nên cache tùy chỉnh ngoài các biến trên cần quản lý bằng chính công cụ đó.

Nếu biến trỏ ra ngoài ổ C, dòng vẫn hiển thị nhưng ghi **Ngoài ổ C · chỉ hiển thị, không quét** và không được đo hay dọn. Với Go/Pub/FVM, runner ghim biến cache vào **đúng đường dẫn hiển thị** (đường đã resolve) để không vô tình dọn cache tùy chỉnh ở nơi khác; biến môi trường của hệ thống không bị thay đổi. CLI cần được cài và có trong PATH; thiếu CLI thì nút Chạy lệnh bị tắt và không chạy lệnh. Lệnh dùng thư mục làm việc của ứng dụng, không chạy trong repository của bạn. Chuyển màn hình, đổi thời gian giữ file hoặc đổi bộ lọc hiển thị sẽ bỏ kết quả quét cũ.

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
- `src/WindowsUpdate.cs`: lưu ý, quyền quản trị, kiểm tra trạng thái cập nhật và thao tác cache với khởi động lại dịch vụ trong finally.
- `src/TempElevation.cs`: khởi chạy lại chính EXE qua UAC để quét/dọn Windows Temp, chỉ nhận lựa chọn tuổi file đã định nghĩa và không nhận lệnh/path xóa.
- `src/RecycleBin.cs`: xác nhận phạm vi, dọn từng mục Thùng rác, audit và luồng STA có xử lý thông điệp.
- `src/ShellRecycleBin.cs`: lấy danh sách mục Shell và xóa riêng từng mục qua IFileOperation, phân biệt xóa/bỏ qua/yêu cầu quyền.
- `src/MainWindow.xaml`: giao diện WPF.
- `tests/CleanerTests.cs`: kiểm thử bằng file và liên kết thật trong fixture.
- `tests/CommandTests.cs`: chạy lệnh xóa trên fixture, CLI giả, kiểm tra quoting, junction, audit, lỗi và hủy tiến trình con; không chạy lệnh dọn thật của Go/Flutter/FVM.
- `tests/RecycleBinTests.cs`: kiểm tra phạm vi, xác nhận cũ, audit, chọn Có/Không, từ chối UAC, bỏ qua rồi tiếp tục, xử lý thông điệp STA; kiểm tra Shell delete với file/thư mục fixture và đọc Thùng rác thật.
- `tests/WindowsUpdateTests.cs`: kiểm tra cache cập nhật trên fixture, mock toàn bộ dịch vụ/WUA, lỗi stop/start, file bị khóa, kiểm tra lại đích và hoàn thành khởi động lại dịch vụ khi đóng Job Object.
- `Build.ps1`: build không cần SDK, chạy kiểm thử khi có `-Test`.

Cơ chế xóa tham khảo API Microsoft: [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) và [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle).
