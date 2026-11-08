# Step 音频驱动构建说明

## 系统要求

- **Windows 10/11 (x64)**
- **.NET 8.0 SDK**
- **Visual Studio 2022** (推荐)

## 快速开始

### 1. 安装 .NET 8.0 SDK

从 [Microsoft 官方](https://dotnet.microsoft.com/download/dotnet/8.0) 下载并安装最新的 .NET 8.0 SDK。

验证安装：
```powershell
dotnet --version
```

应显示 `8.x.x` 或更高版本。

### 2. 使用 Visual Studio 2022

1. 打开 `StepAudioDriver.sln`
2. Visual Studio 会自动还原 NuGet 包
3. 选择 `Step.UI` 为启动项目
4. 按 `F5` 运行

### 3. 使用命令行

```powershell
# 进入项目目录
cd <你的仓库目录>\StepAudioDriver

# 还原依赖
dotnet restore

# 调试构建
dotnet build -c Debug

# 发布构建
dotnet build -c Release

# 运行应用
dotnet run --project src/Step.UI/Step.UI.csproj
```

## 项目配置

### 目标框架

所有项目均针对 `.NET 8.0-windows`：

- **Step.Core** - 类库
- **Step.UI** - WPF 应用程序
- **Step.Service** - 类库
- **测试项目** - 单元测试

### NuGet 依赖

项目自动从 NuGet 还原以下包：

```
NAudio 2.2.1
NAudio.Wasapi 2.2.1
CSCore 1.2.1.2
MathNet.Numerics 5.0.0
HidSharp 2.5.0
Newtonsoft.Json 13.0.3
MahApps.Metro 2.4.10
CommunityToolkit.Mvvm 8.2.2
Serilog 3.0.1
```

## 故障排除

### 错误: "No .NET SDKs were found"

**解决**:
1. 下载安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 重启 Visual Studio 或终端
3. 再次尝试构建

### 错误: "NuGet 包还原失败"

**解决**:
```powershell
# 清除 NuGet 缓存
dotnet nuget locals all --clear

# 重新还原
dotnet restore --force
```

### 编译错误: "类型或命名空间不存在"

**解决**:
1. 检查所有项目文件是否已正确创建
2. 确保 `.csproj` 文件中的项目引用正确
3. 清理并重新构建：
```powershell
dotnet clean
dotnet build
```

## 首次运行

1. **构建成功后**运行应用
2. 应用将尝试：
   - 初始化 AudioEngine
   - 加载预设
   - 连接 SK02 设备（如果已连接）

3. 如果看到错误：
   - 检查 SK02 是否通过 USB 连接
   - 在设备管理器中验证设备是否识别

## 开发工作流

### 推荐的开发环境

**Visual Studio 2022 扩展**:
- C# Dev Kit
- GitHub Copilot (可选)
- ReSharper (可选，付费)

**调试设置**:

1. 在 Visual Studio 中打开 Debug 菜单
2. 选择 "Windows" → "Output" 查看日志
3. 在 "Debug" 窗口中查看实时输出

### 构建命令速查

```powershell
# 快速构建
dotnet build

# 完整清理 + 构建
dotnet clean; dotnet build

# 运行测试
dotnet test

# 发布独立应用（不需要 .NET Runtime）
dotnet publish -c Release -r win-x64 --self-contained

# 仅发布应用（需要系统中有 .NET Runtime）
dotnet publish -c Release -r win-x64 --no-self-contained
```

## 性能优化构建

对于发布版本，使用以下选项获得最佳性能：

```powershell
dotnet publish -c Release -r win-x64 `
  --self-contained `
  -p:PublishReadyToRun=true `
  -p:PublishTrimmed=true
```

这将：
- 预编译 .NET 代码到机器码（R2R）
- 移除未使用的代码（Trimming）
- 创建独立可执行文件

## 制作安装包

安装包使用 WiX 5 生成 MSI，自带 .NET 8 运行时，按当前用户安装（不需要管理员权限）。

```powershell
# 从仓库根目录运行
pwsh installer/build-installer.ps1
```

输出：

```
artifacts/installer/StepAudioDriver-1.0.0.msi
artifacts/installer/StepAudioDriver-1.0.0.sha256
```

脚本会：

1. `dotnet publish` 自包含发布 `src/Step.UI` 到 `artifacts/publish/win-x64`
2. 如本机没有 WiX，则安装到 `artifacts/tools`
3. 编译 `installer/StepAudioDriver.wxs`
4. 生成 SHA256 校验文件

安装包要求：Windows 10/11 x64、已安装 VB-CABLE。详细说明见 `installer/README.md`。

## 后续步骤

构建成功后，参考以下文档：

- **README.md** - 功能介绍和使用方法
- **DEVELOPMENT.md** - 详细开发指南
- **docs/** - 技术文档

## 获取帮助

如遇到问题：

1. 检查 [GitHub Issues](https://github.com/your-repo/issues)
2. 查看 DEVELOPMENT.md 中的故障排除部分
3. 提交新的 Issue 并包含：
   - 完整的错误信息
   - 系统信息 (`dotnet --info`)
   - 重现步骤

---

祝构建顺利！🚀
