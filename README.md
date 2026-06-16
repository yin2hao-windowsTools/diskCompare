# DiskCompare

DiskCompare 是一个 Windows WPF 工具，用于给选定盘符创建文件大小快照，并将快照与该盘当前状态对比，直观看到各个文件夹的体积变化。

## 当前能力

- 枚举固定盘和可移动盘，选择盘符后创建快照。
- NTFS 盘默认优先使用类似 Everything 的元数据索引：首次读取 MFT，后续在缓存可用时读取 USN Journal 增量变更。
- 快速索引不可用时自动回退到目录扫描；两种模式都不读取文件内容。
- 快照保存为 gzip 压缩 JSON，默认目录为程序安装/运行目录下的 `Snapshots`，只新建不覆盖。
- NTFS 增量索引缓存保存到程序安装/运行目录下的 `IndexCache`，只用于加速当前盘后续快照。
- 自动更新下载文件暂存到程序安装/运行目录下的 `Updates`。
- 新建快照默认保存文件夹大小聚合、总大小和文件数，不再逐条保存完整文件路径，减少全盘扫描后的内存占用与 JSON 压缩写盘耗时；旧快照没有聚合时自动回退逐文件聚合。
- 遇到无权限、路径过长、重解析点等目录时跳过并记录错误计数，避免单个目录导致全盘扫描失败。

## 快照逻辑

当前实现采用类似 Everything / WinDirStat 快速 NTFS 扫描的元数据思路：NTFS 本地盘优先只读打开 `\\.\X:` 并解析 MFT 记录，直接从文件系统元数据中构建目录记录树和大小聚合；完成全量 MFT 快照后，会保存一份应用私有索引缓存。后续同一卷的快照如果发现卷序列号、USN Journal ID 和 USN 范围都匹配，则只读取 `FSCTL_QUERY_USN_JOURNAL` 与 `FSCTL_READ_USN_JOURNAL` 返回的增量记录，并按变更记录号重新读取对应 MFT 记录。

如果不是 NTFS、权限不足、USN Journal 不存在、Journal 已断档、缓存不匹配或解析失败，应用会自动回退到完整 MFT 读取；完整 MFT 读取不可用时再回退到普通目录扫描。核心入口位于 `src/DiskCompare.Core/Snapshots/SnapshotBuilder.cs`，快速索引实现位于 `src/DiskCompare.Core/Snapshots/NtfsMftSnapshotProvider.cs`。

NTFS MFT/USN 快速索引通常需要管理员权限或卷读取权限。普通权限运行时应用仍可工作，只是会回退到较慢的目录扫描。

## 数据目录

运行时数据默认集中保存在程序安装/运行目录：

- `Snapshots`: `.dcsnap` 快照文件。
- `IndexCache`: `.ntfsindex` NTFS 索引缓存。
- `Updates`: 自动更新下载与替换脚本暂存文件。

如果应用安装在 `Program Files` 等受保护目录，创建快照、保存索引缓存或自动更新可能需要管理员权限或安装目录写入权限。

## 安全说明

详细安全边界和风险点见 `SAFETY.md`。当前实现不会修改、删除、移动、复制或覆盖被扫描盘上的任何文件；写入仅限于创建新的快照文件和新的应用私有 NTFS 索引缓存文件。

## 本地运行

```powershell
dotnet build DiskCompare.slnx
dotnet run --project src\DiskCompare.App\DiskCompare.App.csproj
```

## 发布包说明

- 发布产物不再内置 .NET 8，以减小安装包和 portable ZIP 体积。
- `DiskCompare.exe` 现在是一个轻量启动器；它会先检查本机是否已安装 `.NET 8 Desktop Runtime`。
- 如果未安装，启动器会弹窗提示，并打开微软官方 `.NET 8` 下载页，用户自行安装后即可继续运行。
