# Independent channel Ref/Sig processing

- Status: `FAILED`
- Recorded: 2026-08-26T17:23:33.4154469+09:00
- Wall time: 0.019 s

## Failure

```
System.IO.IOException: 'C:\Project\OFDR\Datas\OFDR Raw data\converted_channels\0_0mm0000CH1.csv' 파일은 다른 프로세스에서 사용 중이므로 프로세스에서 액세스할 수 없습니다.
   위치: System.IO.__Error.WinIOError(Int32 errorCode, String maybeFullPath)
   위치: System.IO.FileStream.Init(String path, FileMode mode, FileAccess access, Int32 rights, Boolean useRights, FileShare share, Int32 bufferSize, FileOptions options, SECURITY_ATTRIBUTES secAttrs, String msgPath, Boolean bFromProxy, Boolean useLongPath, Boolean checkHost)
   위치: System.IO.FileStream..ctor(String path, FileMode mode, FileAccess access, FileShare share, Int32 bufferSize, FileOptions options)
   위치: OFDR_Signal_Core.Services.OfdrChannelFileReader.ReadCsv(String path, Int32 expectedChannel, CancellationToken token, IProgress`1 progress) 파일 C:\Project\OFDR\OFDR_Signal_Generator\OFDR_Signal_Core\Services\OfdrChannelFileReader.cs:줄 53
   위치: OFDR_Signal_Core.Services.OfdrChannelFileReader.Read(String path, Int32 expectedChannel, CancellationToken cancellationToken, IProgress`1 progress) 파일 C:\Project\OFDR\OFDR_Signal_Generator\OFDR_Signal_Core\Services\OfdrChannelFileReader.cs:줄 24
   위치: OFDR_Harness.Program.RunPair(IDictionary`2 options) 파일 C:\Project\OFDR\OFDR_Signal_Generator\OFDR_Harness\Program.cs:줄 241
```
