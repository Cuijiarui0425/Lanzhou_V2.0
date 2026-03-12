# Lanzhou_v1.0 项目文件树

> 工作区路径：`c:\Users\Cui Jiarui\Desktop\嵌入式求职\项目1：兰州物化所电机控制系统\copilottest\test1`

```text
test1/
├─ App.xaml
├─ App.xaml.cs
├─ AssemblyInfo.cs
├─ Lanzhou_v1.0.csproj
├─ Lanzhou_v1.0.csproj.user
├─ Lanzhou_v1.0.sln
├─ MainViewModel.cs
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ Camera/
│  ├─ CameraDeviceDescriptor.cs
│  ├─ CameraFrame.cs
│  ├─ CameraSelectWindow.xaml
│  ├─ CameraSelectWindow.xaml.cs
│  └─ EbusCameraService.cs
├─ config/
│  ├─ plc_error_code_map.csv
│  ├─ pointmap_mvp.csv
│  └─ servo_alarm_map.csv
├─ DAQ/
│  ├─ DaqConfig.cs
│  ├─ DaqSample.cs
│  └─ Pcie1805BufferedDaqService.cs
├─ Lib/
├─ obj/
│  ├─ Lanzhou_v1.0.csproj.nuget.dgspec.json
│  ├─ Lanzhou_v1.0.csproj.nuget.g.props
│  ├─ Lanzhou_v1.0.csproj.nuget.g.targets
│  ├─ project.assets.json
│  └─ Debug/
│     └─ net8.0-windows/
│        ├─ Lanzhou_v1.0.AssemblyInfo.cs
│        ├─ Lanzhou_v1.0.GeneratedMSBuildEditorConfig.editorconfig
│        ├─ Lanzhou_v1.0.GlobalUsings.g.cs
│        ├─ ref/
│        └─ refint/
├─ PLC/
│  ├─ ModbusPlcClient.cs
│  ├─ PlcErrorCodeCatalog.cs
│  ├─ PlcPointMap.cs
│  └─ ServoAlarmCatalog.cs
├─ Properties/
│  └─ PublishProfiles/
│     ├─ FolderProfile.pubxml
│     └─ FolderProfile.pubxml.user
└─ Sensors/
   └─ StrainTransmitterService.cs