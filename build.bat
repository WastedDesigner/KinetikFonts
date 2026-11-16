@echo off
rem Publishes a framework-dependent single-file KinetikFonts.exe into dist\
dotnet publish "%~dp0KinetikFonts.csproj" -c Release -p:PublishSingleFile=true --self-contained false -o "%~dp0dist"
