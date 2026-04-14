FROM python:3.12-slim

WORKDIR /app

COPY bridge/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY bridge /app

ENV DEYE_BRIDGE_CONFIG=/config/bridge-config.json
ENV DEYE_BRIDGE_STATE=/state/bridge-state.json
ENV DEYE_BRIDGE_POLL_INTERVAL=2
ENV DEYE_BRIDGE_REQUEST_TIMEOUT=10
ENV DEYE_BRIDGE_PORT=8081

EXPOSE 8081

CMD ["sh", "-c", "uvicorn app:app --host 0.0.0.0 --port ${DEYE_BRIDGE_PORT}"]
