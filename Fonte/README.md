cd C:\AplicacoesOnline
Import-Module WebAdministration
Stop-WebAppPool -Name "AplicacoesOnline"
powershell -ExecutionPolicy Bypass -File .\scripts\publicar-producao.ps1 -IisAppPoolName "AplicacoesOnline"
Start-WebAppPool -Name "AplicacoesOnline"