# DiskCompare 安全审计

目标：应用不能修改、删除、移动、复制或覆盖用户磁盘上的被扫描文件。所有磁盘扫描都应当是元数据读取。

## 已加固的安全边界

- 扫描盘符时只调用 `DirectoryInfo.EnumerateDirectories`、`DirectoryInfo.EnumerateFiles` 和 `FileInfo` 属性读取，不打开文件内容，不写入被扫描盘。
- 跳过重解析点，避免进入 junction、symlink 等可能指向盘外或形成循环的路径。
- 无权限、路径过长、IO 异常等被记录为扫描错误并跳过，不尝试修复或修改文件权限。
- 快照保存只允许写入 `SnapshotStore` 管理的快照目录，默认是 `%LOCALAPPDATA%\DiskCompare\Snapshots`。
- 快照文件必须使用 `.dcsnap` 扩展名，并使用 `FileMode.CreateNew` 创建，拒绝覆盖已有文件。
- 快照加载只接受已存在的 `.dcsnap` 文件，并以只读方式打开。
- 写入快照前会拒绝经过重解析点的快照目录，避免通过 junction、symlink 等把快照写到意外位置。
- 测试清理只允许删除自己创建的 `%TEMP%\DiskCompare.Tests\...` 目录，删除前校验目录边界，不做递归删除。

## 仍需标出的风险点

这些点不应损坏用户文件，但属于有外部环境影响或资源风险的位置：

- `SnapshotBuilder` 会遍历所选盘符的目录元数据。风险：大盘扫描会产生磁盘读取负载，可能让机械硬盘或网络盘变慢；不会写入或删除文件。
- `SnapshotBuilder` 读取 `FileInfo.Length` 和 `LastWriteTimeUtc`。风险：某些文件系统配置可能因为访问元数据而更新最后访问时间，某些特殊文件系统驱动也可能在读取元数据时触发自身行为；应用代码不写入文件内容、不改时间戳、不改属性。
- `SnapshotStore.SaveAsync` 会在快照目录创建 `.dcsnap` 文件。风险：会占用用户本机空间；已限制到快照目录且拒绝覆盖。
- `MainWindow.OpenSnapshotFolder_Click` 会创建快照目录并用系统 shell 打开它。风险：会创建 `%LOCALAPPDATA%\DiskCompare\Snapshots` 目录；不会修改其他文件。
- `OpenFileDialog` 可读取用户选择的 `.dcsnap`。风险：恶意或损坏的快照文件可能导致加载失败或内存占用较高；加载逻辑只读，不写入该文件。
- 路径校验和文件创建之间仍存在本机其他进程恶意替换目录的 TOCTOU 风险。当前实现会在创建快照目录前后检查重解析点，并使用 `FileMode.CreateNew` 拒绝覆盖，但没有使用底层 Win32 句柄锁定目录。
- 如果底层磁盘、文件系统过滤驱动、杀毒软件或同步软件对“读取元数据”有副作用，应用无法完全控制这些外部组件；本应用自身不发起文件内容写入、覆盖、移动或删除。

## 当前禁止事项

- 不删除用户选择的磁盘中的任何文件。
- 不覆盖已有快照文件。
- 不写入快照目录之外的位置。
- 不跟随重解析点扫描到其他位置。
- 不修改 ACL、时间戳、属性或文件内容。
