# SDK 10.0.401 and Playwright 1.63.0 with Chromium/Firefox/WebKit dependencies.
FROM mcr.microsoft.com/playwright/dotnet:v1.63.0-noble@sha256:aa3eaf29a65df8c93fc32ab20cf9359f4d8ecc53e6e89f79378647ace87b46b3
USER root
RUN sed -i 's|http://azure.archive.ubuntu.com|http://archive.ubuntu.com|g' /etc/apt/sources.list.d/ubuntu.sources && apt-get update && apt-get install -y --no-install-recommends ripgrep util-linux ca-certificates && rm -rf /var/lib/apt/lists/*
COPY install-tools.sh /tmp/install-tools.sh
RUN bash /tmp/install-tools.sh && rm /tmp/install-tools.sh && test "$(dotnet --version)" = 10.0.401
WORKDIR /workspace/home-assistant-supervisor-operator
COPY --chown=pwuser:pwuser .smoke/ /opt/haso-browser-smoke/
RUN chown pwuser:pwuser /workspace/home-assistant-supervisor-operator
USER pwuser
CMD ["bash"]
