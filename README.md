# BlueSend

蓝牙文字通信 + 文件传输工具。两台 Windows 电脑通过蓝牙配对后，即可收发文字消息和文件。

## 功能

- 文字聊天（服务端/客户端模式）
- 文件发送（左键点击打开，右键另存为）
- 蓝牙地址一键复制
- 自动保存客户端连接记录

## 使用

1. 先在**服务端**点击「创建聊天」，复制蓝牙地址给对方
2. 在**客户端**点击「加入聊天」，输入服务端蓝牙地址，点击连接
3. 连接成功后即可聊天和发送文件

## 构建

```bash
dotnet restore
dotnet build
```

发布单文件：

```bash
dotnet publish -r win-x64 -c Release -o publish --self-contained true -p:PublishSingleFile=true
```

或直接双击 `build.bat`。

## 依赖

- .NET 9
- [InTheHand.Net.Bluetooth](https://www.nuget.org/packages/InTheHand.Net.Bluetooth) (32feet.NET)

## 要求

- Windows 10 / 11（支持 Bluetooth）
- 两台电脑已通过 Windows 蓝牙设置完成配对
- .NET 9 运行时（如使用自包含发布则无需）
