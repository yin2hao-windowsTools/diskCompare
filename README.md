# DiskCompare

DiskCompare 是一个 Windows WPF 工具，用于给选定盘符创建文件大小快照，并将快照与该盘当前状态对比，直观看到各个文件夹的体积变化。

## 当前能力

- 枚举固定盘和可移动盘，选择盘符后创建快照。
- 快照只记录相对路径、文件大小和修改时间，不读取文件内容。
- 快照保存为 gzip 压缩 JSON，默认目录为 `%LOCALAPPDATA%\DiskCompare\Snapshots`。
- 对比时重新扫描当前磁盘，并展示总变化、最大变化列表和文件夹变化树。
- 遇到无权限、路径过长、重解析点等目录时跳过并记录错误计数，避免单个目录导致全盘扫描失败。

## 快照逻辑

首版采用类似 Everything 初始索引的元数据思路：只遍历文件系统目录项和文件属性，不做内容读取、哈希或复制。核心扫描代码位于 `src/DiskCompare.Core/Snapshots/SnapshotBuilder.cs`，已保留独立边界，后续可以替换为 NTFS MFT/USN Provider 来获得更接近 Everything 的速度。

## 本地运行

```powershell
dotnet build DiskCompare.slnx
dotnet run --project src\DiskCompare.App\DiskCompare.App.csproj
```
