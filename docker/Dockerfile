# Stage 1: Build frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm install --frozen-lockfile 2>/dev/null || npm install
COPY frontend/ .
RUN npm run build

# Stage 2: Python backend + built frontend
FROM python:3.12-slim
WORKDIR /app

# Install system dependencies for lm-sensors
RUN apt-get update && apt-get install -y --no-install-recommends \
    lm-sensors \
    && rm -rf /var/lib/apt/lists/*

# Install Python dependencies
COPY backend/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Create non-root user before COPY so --chown works
RUN useradd --system --create-home --home-dir /home/drivechill drivechill \
    && mkdir -p /app/data \
    && chown drivechill:drivechill /app/data /home/drivechill

# Copy backend code (owned by drivechill, avoids extra chown layer)
COPY --chown=drivechill:drivechill backend/ ./backend/

# Copy built frontend
COPY --chown=drivechill:drivechill --from=frontend-build /app/frontend/out ./frontend/out

ENV DRIVECHILL_HARDWARE_BACKEND=auto
ENV DRIVECHILL_HOST=0.0.0.0
ENV DRIVECHILL_PORT=8085

EXPOSE 8085

USER drivechill

WORKDIR /app/backend
CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8085"]
