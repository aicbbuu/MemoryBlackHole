# 构建、打包与发布说明 (Build & Package)

## 目标

- Windows x64
- .NET 8 self-contained
- 单文件 EXE
- 自签名开发证书
- Inno Setup 安装包

> 当前版本仅发布 `win-x64`。旧版同时编 `win-x86`，但 x86 进程用户地址空间仅 2~4 GB，
> 写入几百 MB 以上的 BLOB 时 SQLite 事务容易触发 `SQLITE_NOMEM (error 7)`。
> x64 进程地址空间充裕，代码可稳定存到 800MB（十进制）以内。阈值刻意小于 SQLite 默认 `SQLITE_MAX_LENGTH=1,000,000,000` 字节，以避开编译期 BLOB 上限。

## Windows 环境准备

1. 安装 .NET 8 SDK（x64）。
2. 安装 Windows SDK（提供 `signtool.exe`）。
3. 安装 Inno Setup（提供 `ISCC.exe`）。

## 一键正式打包

在项目根目录打开 PowerShell：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\packaging\build-release.ps1
```

脚本会依次：

1. 发布 `win-x64` self-contained 单文件 EXE；
2. 首次运行时创建本地开发自签名证书；
3. 使用 SHA-256 签署 EXE，并执行签名验证；
4. 编译 Inno Setup 安装包。

产物位置：

```text
artifacts/publish/win-x64/MemoryBlackHole.exe
artifacts/certificate/MemoryBlackHole-dev.pfx
artifacts/certificate/MemoryBlackHole-dev.cer
artifacts/installer/MemoryBlackHole-Setup-3.0.0-win-x64.exe
```

## 证书说明

这是开发/测试用自签名证书，不是受 Windows 信任的商业代码签名证书。首次在其他电脑运行时可能显示"未知发布者"。

`.pfx` 包含私钥，不要上传 GitHub 或发送给他人。

自签名证书在本机通常会显示 `Unknown`，这不代表 EXE 没有签名。签名脚本会区分"签名存在但未受信任"和"签名损坏"两种情况；前者会给出警告并继续打包，后者才会终止。

## 安装目录和数据库

安装程序默认安装到当前用户目录：

```text
%LOCALAPPDATA%\Programs\MemoryBlackHole\
```

数据库会保存在安装目录下：

```text
.memoryblackhole\memory.db
.memoryblackhole\files\        （超过 800MB BLOB 阈值的文件副本存放目录）
.memoryblackhole\memory.db-wal / -shm  （WAL 模式日志）
```

选择当前用户安装位置是为了让程序具备正常写入数据库的权限，避免 `Program Files` 权限问题。