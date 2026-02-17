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

# Copy backend code
COPY backend/ ./backend/

# Copy built frontend
COPY --from=frontend-build /app/frontend/out ./frontend/out

# Create data directory
RUN mkdir -p /app/data

ENV DRIVECHILL_HARDWARE_BACKEND=auto
ENV DRIVECHILL_HOST=0.0.0.0
ENV DRIVECHILL_PORT=8085

EXPOSE 8085

WORKDIR /app/backend
CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8085"]
