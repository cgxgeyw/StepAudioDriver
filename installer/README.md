# Step 音频驱动 安装包

## 文件

- `StepAudioDriver-1.0.0.msi`：Windows 安装包（64 位，自带 .NET 8 运行时）
- `StepAudioDriver-1.0.0.sha256`：安装包校验值

## 系统要求

- Windows 10 / 11，64 位
- 已安装 **VB-CABLE**（捕获设备 `CABLE Input` / `CABLE In 16ch`）
- 输出设备：SK02、N9V2 或其它 WASAPI 播放设备

VB-CABLE 不随安装包分发，请从 [VB-Audio 官网](https://vb-audio.com/Cable/) 单独安装。

## 安装

1. 双击 `StepAudioDriver-1.0.0.msi`
2. 按提示完成；安装范围为**当前用户**，不需要管理员权限
3. 开始菜单和桌面会创建 `Step 音频驱动` 快捷方式

安装目录：

```
%LOCALAPPDATA%\Programs\Step 音频驱动
```

卸载：Windows 设置 → 应用 → 已安装的应用 → Step 音频驱动。

## 注意

- N9V2 / ATK 的 `FlowAudioDevice.sys` 驱动版本较老，独占初始化可能蓝屏；程序已强制该设备走共享模式，但驱动仍需更新
- 首次启动如提示缺少 VB-CABLE，请先安装虚拟声卡并重启
- 安装包未签名，SmartScreen 提示时选择“更多信息 → 仍要运行”
