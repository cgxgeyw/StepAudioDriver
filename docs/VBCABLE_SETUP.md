# VB-CABLE 安装和配置指南

## 下载和安装

1. **下载 VB-CABLE**
   - 访问：https://vb-audio.com/Cable/
   - 下载 "CABLE Input (VB-Audio Virtual Cable)"
   - 或者直接下载：https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip

2. **安装步骤**
   ```
   1. 解压 ZIP 文件
   2. 右键 VBCABLE_Setup_x64.exe → "以管理员身份运行"
   3. 点击 "Install Driver"
   4. 重启电脑（推荐）
   ```

3. **验证安装**
   - 右键任务栏音量图标 → "声音设置"
   - "输出设备" 下拉菜单应该看到 "CABLE Input (VB-Audio Virtual Cable)"

## 配置三角洲行动

### 游戏内音频设置

1. **启动三角洲行动**
2. **进入设置 → 音频**
3. **输出设备选择**：
   - 将输出设备改为 **"CABLE Input (VB-Audio Virtual Cable)"**
   - 声道配置：选择 **"7.1 环绕声"**（如果有）
   - 如果只有立体声选项，保持立体声

### Windows 音频设置

1. **打开 Windows 声音控制面板**
   ```
   Win + R → mmsys.cpl → 回车
   ```

2. **播放设备配置**
   - 找到 "CABLE Input"
   - 右键 → "配置扬声器"
   - 选择 **"7.1 环绕声"**
   - 完成向导

3. **默认设备设置**
   - 将 "CABLE Input" 设为 **默认设备**
   - 将你的 SK02 保持为备用（或者不动）

## 验证音频流

1. **测试音频捕获**
   ```powershell
   # 播放任何音频（如 YouTube 视频）
   # 在任务管理器 → 性能 → 音频中应该看到 CABLE Input 有活动
   ```

2. **监听测试**（可选）
   - 右键 "CABLE Input" → "属性"
   - "侦听" 标签页 → 勾选 "侦听此设备"
   - "通过此设备播放" → 选择你的 SK02
   - 现在应该能听到声音了

## Step 音频驱动设置

1. **保持 SK02 为实际输出**
   - 在 Windows 声音设置中
   - SK02 应该显示为可用的输出设备
   - 我们的程序会捕获 CABLE Input 的音频，处理后输出到 SK02

## 架构流程

```
三角洲行动
    ↓ (7.1 音频)
CABLE Input (虚拟 7.1 声卡)
    ↓ (WASAPI Loopback 捕获)
Step 音频驱动
    ├─ HRTF 7.1 虚拟化
    ├─ EQ 处理
    └─ 音量控制
    ↓ (WASAPI 独占输出)
Fosi Audio SK02 (硬件)
    ↓
Sony Z7M2 耳机
```

## 故障排除

### 问题 1: 游戏找不到 7.1 选项

**解决**：
- 有些游戏只检测物理设备的声道数
- 保持立体声输出，我们在驱动中虚拟 7.1

### 问题 2: 听不到声音

**检查**：
1. CABLE Input 是否设为默认设备
2. 游戏音频是否输出到 CABLE Input
3. 我们的驱动程序是否正在运行

### 问题 3: 音频卡顿

**原因**：缓冲区太小
**解决**：在驱动设置中增加缓冲区大小（256 → 512）

## 卸载 VB-CABLE

如果需要卸载：
1. 右键 VBCABLE_Setup_x64.exe → "以管理员身份运行"
2. 点击 "Uninstall Driver"
3. 重启电脑

---

**准备好后，运行我们的驱动程序即可开始处理音频。**
