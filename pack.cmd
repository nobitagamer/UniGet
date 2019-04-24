@echo off

pushd %~dp0

call build.cmd assemblyinfo
call build.cmd pack %*

popd
