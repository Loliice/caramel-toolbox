# 焦糖工具箱 (Caramel Toolbox)

一套面向 **VRChat 换装 / 改模工作流** 的 Unity 编辑器工具集，把日常高频操作收进一个统一的「焦糖工具箱」面板，一键直达。

> Unity 2022.3 · VRChat SDK3 · 需要 Modular Avatar + NDMF

## 功能一览

所有工具统一入口在 **「焦糖工具箱」** 菜单下，也可通过「工具箱面板」一键打开：

| 工具 | 说明 |
|------|------|
| 快速改模 | 新增服装 / 发型 / 配件，槽位式管理，支持临时预览 |
| 快速修复穿模 | 一键修复胸、脚穿模 |
| 快速上传 | 跳过 SDK 面板校验步骤，直接填名称与封面并上传 |
| 静默导入 | 拖入 `.unitypackage` / 压缩包静默导入，支持 7z / zip / rar 嵌套解压 |
| 材质批量替换 | 批量替换材质引用 |
| 快速贴图压缩 | 批量压缩贴图、统计显存 |
| RGB 通道拆分 | 把一张贴图按 R / G / B 拆成三张灰度图 |
| 商品图预览 | 扫描各商品目录预览图，自适应网格浏览 |
| 动骨计算工具 | 统计 PhysBone 数量 |
| 快速复制场景 | 复制当前场景 |

## 安装

### 方式一：VCC（推荐）

1. 打开 [VCC（VRChat Creator Companion）](https://vrchat.com/download/vcc)。
2. 通过 VPM 包仓库添加本包（见下方「VPM 仓库」）。
3. 在项目里点「Manage Project」→ 勾选「焦糖工具箱」→ 应用。

### 方式二：手动安装

1. 下载本仓库。
2. 把仓库根目录（包含 `package.json`）作为一个 Unity 包导入，或把 `Editor/` 目录复制到工程的 `Assets/CaramelToolbox/` 下。

### VPM 仓库

将以下 URL 添加为 VCC 的 Community Repo：

```
https://loliice.github.io/caramel-toolbox/index.json
```

（若尚未发布该地址，请以本仓库实际部署的 VPM listing 为准。）

## 依赖

本工具集**必须**依赖以下包（均为 VRChat 换装工作流的常见依赖）：

- [VRChat Avatars SDK3](https://vrchat.com/download/vcc)（`com.vrchat.avatars` ≥ 3.7.0）
- [Modular Avatar](https://github.com/bdunderscore/modular-avatar)（`nadena.dev.modular-avatar` ≥ 1.10.0）
- [NDMF](https://github.com/bdunderscore/ndmf)（`nadena.dev.ndmf` ≥ 1.6.0）

此外，静默导入功能内置了 [SharpCompress](https://github.com/adamhathcock/sharpcompress)（MIT 许可）用于解压 7z / zip / rar 压缩包，已随包附带 `SharpCompress.dll`。

## 目录结构

```
caramel-toolbox/
├── package.json          # VPM / UPM 包描述
├── Editor/               # 所有编辑器脚本与资源
│   ├── CaramelLauncherWindow.cs   # 工具箱启动面板
│   ├── CaramelUI.cs               # 统一 UI 样式
│   ├── FastEdit/                  # 快速改模 / 穿模修复
│   ├── FastUpload/                # 快速上传
│   ├── MaterialTool/              # 材质替换 / 贴图压缩 / 通道拆分
│   ├── PackageImporter/           # 静默导入（含 SharpCompress.dll）
│   ├── ImageGallery/              # 商品图预览
│   ├── PhysBoneTool/              # 动骨计算
│   └── SceneTool/                 # 场景复制
└── LICENSE
```

## 已知事项

- 部分菜单资源文件名含中文与 `&` 字符（如 `Clothes&Hair&配饰道具妆容.prefab`），这是工程内既有命名，请勿重命名，否则会破坏 Unity 的 GUID 关联。
- 静默导入解压 RAR 时，若文件名含非 UTF-8 编码（如 GBK 中文），可能因 Unity 未内置 `System.Text.Encoding.CodePages` 而失败；zip / 7z / tar / gz 不受影响。

## 许可

[MIT](LICENSE) © Loliice

依赖库许可：
- SharpCompress：MIT
