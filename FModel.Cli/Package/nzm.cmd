@echo off
"%~dp0FModel.Cli.exe" %* --profile "%~dp0nzm.local.json"
exit /b %errorlevel%
