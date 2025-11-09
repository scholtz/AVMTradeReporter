#!/bin/bash

# Build and run the services using docker-compose

if [ "$ver" == "" ]; then
ver=1.0.0
fi

echo "docker build -t \"scholtz2/avm-trade-reporter-subscriber:$ver-main\" -f AVMTradeReporter.Subscriber/Dockerfile ."
docker build -t "scholtz2/avm-trade-reporter-subscriber:$ver-main" -f AVMTradeReporter.Subscriber/Dockerfile . || error_code=$?
error_code_int=$(($error_code + 0))
if [ $error_code_int -ne 0 ]; then
  echo "failed to build";
  exit 1;
fi

docker push "scholtz2/avm-trade-reporter-subscriber:$ver-main" || error_code=$?
error_code_int=$(($error_code + 0))
if [ $error_code_int -ne 0 ]; then
  echo "failed to push";
  exit 1;
fi

echo "Image: scholtz2/avm-trade-reporter-subscriber:$ver-main"


echo "docker build -t \"scholtz2/avm-trade-reporter:$ver-main\" -f AVMTradeReporter/Dockerfile ."
docker build -t "scholtz2/avm-trade-reporter:$ver-main" -f AVMTradeReporter/Dockerfile . || error_code=$?
error_code_int=$(($error_code + 0))
if [ $error_code_int -ne 0 ]; then
  echo "failed to build";
  exit 1;
fi

docker push "scholtz2/avm-trade-reporter:$ver-main" || error_code=$?
error_code_int=$(($error_code + 0))
if [ $error_code_int -ne 0 ]; then
  echo "failed to push";
  exit 1;
fi

echo "Image: scholtz2/avm-trade-reporter:$ver-main"