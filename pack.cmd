@echo off

pushd %~dp0

call build.cmd pack %*

popd
