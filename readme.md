# Device Data Collector

A .NET 8 web application for collecting, storing, and managing data from TCP/IP devices.

## Project Overview

This application serves as a central data collection point for IoT or networked devices that communicate via TCP/IP. It runs a TCP server that listens for incoming connections, stores received data in a MySQL database, and provides a web interface for viewing and managing the collected data.

## Features

- **TCP/IP Server**: Listens on a configurable port (default: 5000) for incoming connections
- **Device Communication**: Send and receive messages from connected devices
- **Data Storage**: Automatically stores incoming data in a MySQL database
- **Web Interface**: View, create, edit, and delete stored device data
- **Live Communication**: Test device connectivity by sending manual messages

## System Architecture

The application consists of:

1. **Web Interface** (ASP.NET MVC)
2. **TCP Server** (Background Service)
3. **MySQL Database** (via Entity Framework Core)

## Getting Started

### Prerequisites

- .NET 8.0 SDK
- MySQL Server
- Visual Studio 2022 or later (recommended)

### Configuration

Database and TCP server settings can be configured in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "server=localhost;port=3306;database=devicedata;user=root;password=root"
  },
  "TCPServer": {
    "IPAddress": "127.0.0.2",
    "Port": 5000
  }
}
```

### Running the Application

1. Clone the repository
2. Update the database connection string in `appsettings.json`
3. Apply database migrations:
   ```
   dotnet ef database update
   ```
4. Run the application:
   ```
   dotnet run
   ```

The application will start, and the TCP server will begin listening on the configured IP address and port.

## Key Components

### Controllers

- **HomeController**: Main application entry point
- **DeviceController**: Handles device communication
- **DeviceDataController**: Manages stored device data

### Services

- **TCPServerService**: Background service that maintains a TCP listener and processes incoming connections
- **TCPClientService**: Handles outgoing TCP connections to devices

### Data Model

- **DeviceData**: Represents a single data packet received from a device
  - ID (auto-generated)
  - DeviceID (string identifier)
  - Timestamp (when data was received)
  - DataPayload (the actual data content)
  - IPAddress (source IP)
  - Port (source port)

## Usage

### Viewing Collected Data

Navigate to the "View Data" page to see all data collected from devices.

### Sending Messages to Devices

Use the "Send Data" page to manually send messages to devices:

1. Enter the device IP address and port
2. Type your message
3. Click "Send Message"

The system will attempt to connect to the device, send the message, and display any response.

### Testing with TCP Client Tools

For testing, you can use tools like:

- **Hercules** (Set to 127.0.0.3 in the default configuration)
- **NetCat**
- **Telnet**

## Development Notes

- The application is configured to distinguish between messages sent from the web interface and actual device data.
- Messages sent via the web interface are not stored in the database.
- The server automatically adds a newline character to messages if not present.

## Database Management

The project uses Entity Framework Core with a Code-First approach. To manage database migrations:

```bash
# Add a new migration
dotnet ef migrations add MigrationName

# Update the database to the latest migration
dotnet ef database update
```

## Project Structure

- **Controllers/**: MVC controllers
- **Models/**: Data models
- **Views/**: User interface
- **Services/**: Background services and utilities
- **Data/**: Database context and configuration
- **Migrations/**: Database migration files

## License

[Your license information]

## Contributors

[Your contributor information]
