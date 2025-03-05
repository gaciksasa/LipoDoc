# Blood Donation Data Collector

A .NET 8 web application designed to collect, store, and manage data from blood donation devices over TCP/IP.

## Overview

The Blood Donation Data Collector is a centralized system that provides:

- Real-time data collection from blood lipemic testing devices
- Secure data storage in a MySQL database
- User-friendly web interface for monitoring and managing devices
- Role-based access control for system security
- Comprehensive donation data analytics and reporting

This application serves as a hub for blood donation centers to monitor their testing devices, collect lipemic test results, and ensure data integrity across multiple locations.

## System Features

### Device Communication
- TCP/IP server that listens for incoming connections from testing devices
- Automatic device registration and status tracking
- Support for various message formats using standardized protocols
- Buffered data retrieval for devices with intermittent connectivity

### Data Management
- Automatic parsing and storage of device messages
- Comprehensive blood donation test data storage
- Device status monitoring and tracking
- Historical data retention and cleanup

### Web Interface
- Real-time device monitoring dashboard
- Detailed donation data visualization
- Device management and configuration
- User-friendly navigation and responsive design

### Security
- Role-based authentication system (Admin/User roles)
- Secure password storage with BCrypt hashing
- Protected routes and views based on user permissions
- Audit logging for sensitive operations

## System Architecture

The application consists of:

1. **Web Interface** - Built with ASP.NET MVC
2. **TCP Server** - Background service for device communication
3. **Database** - MySQL with Entity Framework Core
4. **Background Services** - For monitoring, cleanup, and maintenance tasks

## Database Schema

The application uses the following key tables:

### DonationsData
Stores the lipemic test results from blood donation devices:
- ID (auto-generated primary key)
- DeviceId (serial number of the device)
- Timestamp (when data was received)
- MessageType (the type of message: "#S", "#D", etc.)
- RawPayload (the raw message content)
- IP/Port information
- Device status information
- Barcode data (donation ID, operator ID, ref code, lot number)
- Lipemic test results (value, group, status)

### DeviceStatus / CurrentDeviceStatus
Stores the status updates from devices:
- DeviceId (serial number of the device)
- Timestamp (when status was received)
- Status (0=IDLE, 1=Process in progress, 2=Process completed)
- AvailableData (number of readings buffered in the device)
- IP/Port information

### Device
Stores information about registered devices:
- ID (auto-generated primary key)
- SerialNumber (unique identifier for the device)
- Name (friendly name for the device)
- Location (location of the device)
- LastConnectionTime (last time the device connected)
- RegisteredDate (when the device was first registered)
- IsActive (whether the device is active)
- Notes (additional notes about the device)

### Users
Stores user authentication information:
- ID (auto-generated primary key)
- Username (unique username)
- PasswordHash (BCrypt-hashed password)
- Role (Admin or User)
- FullName, Email, CreatedAt, LastLogin

## Setting Up the Application

### Prerequisites

- .NET 8.0 SDK
- MySQL Server 8.0 or higher
- An IDE like Visual Studio 2022 or VS Code

### Configuration

The main configuration is in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "server=localhost;port=3306;database=devicedata;user=root;password=root"
  },
  "TCPServer": {
    "IPAddress": "192.168.1.124",
    "Port": 5000
  },
  "DeviceStatusMonitor": {
    "CheckIntervalSeconds": 5,
    "InactiveThresholdSeconds": 10
  },
  "DeviceStatusCleanup": {
    "IntervalHours": 24,
    "RetentionDays": 30
  }
}
```

Key settings:
- **DefaultConnection**: Your MySQL connection string
- **TCPServer:IPAddress**: The IP address to listen on for device connections
- **TCPServer:Port**: The port to listen on for device connections
- **DeviceStatusMonitor**: Configuration for device status checking
- **DeviceStatusCleanup**: Configuration for automatic data cleanup

### Running the Application

1. Clone the repository
2. Update the MySQL connection string in `appsettings.json`
3. Run the database migrations:
   ```
   dotnet ef database update
   ```
4. Start the application:
   ```
   dotnet run
   ```

The application will:
- Initialize the database (create if it doesn't exist)
- Apply any pending migrations
- Start the TCP server for device communication
- Launch the web interface

## Authentication

The system includes two predefined user accounts:

1. **Administrator**
   - Username: `admin`
   - Password: `admin123`
   - Full access to all features

2. **Regular User**
   - Username: `user`
   - Password: `user123`
   - Limited access (can view but not modify data)

## Device Communication Protocol

Devices communicate with the server using a text-based protocol with the following format:

### Status Messages
```
#SªSNªStatusª"timestamp"ª"AvailableData"ª"CS"ý
```

### Data Messages
```
#DªSNªtimestampªBªRefCodeªDonationIdªOperatorIdªLotNumberªMªLipemicValueªLipemicGroupªLipemicStatusªCSý
```

Where:
- `#S` or `#D` indicates message type (Status or Data)
- `ª` is the field separator (Unicode 170)
- `SN` is the device serial number
- `B` indicates barcode mode (followed by barcode data)
- `M` indicates measurement data (followed by lipemic test results)
- `ý` marks the end of the message

The application also supports requesting buffered data from devices using:
```
#uªSNª\n
```

And acknowledges received data with:
```
#AªSNª\n
```

## Background Services

The application includes several background services:

1. **TCPServerService** - Listens for incoming connections from devices
2. **DeviceStatusMonitorService** - Monitors device status and marks devices as inactive when appropriate
3. **DeviceStatusCleanupService** - Performs periodic cleanup of historical status data
4. **BufferDataRetrievalService** - Retrieves buffered data from devices on demand

## User Interface

The web interface is organized into several main sections:

1. **Home** - Overview and system status
2. **Donations** - View and manage donation records
3. **Devices** - Monitor and manage connected devices
4. **User Management** (Admin only) - Add, edit, and delete user accounts

## Extending the Application

### Adding New Device Types

To add support for new device types:

1. Update the `DeviceMessageParser` class to handle new message formats
2. Add any new fields to the `DonationsData` model if needed
3. Create migrations to update the database schema

### Adding New Features

The modular design makes it easy to add new features:

1. Create new controller(s) for the feature
2. Add corresponding model(s) and views
3. Update the navigation in `_Layout.cshtml`
4. Add any necessary background services

## Troubleshooting

### Common Issues

1. **Database Connection Errors**
   - Verify your MySQL connection string
   - Ensure MySQL is running
   - Check user permissions

2. **TCP Server Not Starting**
   - Verify the IP address is available on your system
   - Check if the port is already in use
   - Look for firewall restrictions

3. **Devices Not Connecting**
   - Verify device is configured with correct server IP and port
   - Check network connectivity between device and server
   - Review server logs for connection attempts

### Logging

The application uses structured logging to help diagnose issues:

- Logs are written to the console by default
- Adjust log levels in `appsettings.json` to increase or decrease verbosity
- For production, consider configuring a more robust logging provider

## License

[Your License Information]

## Support

For questions or support, please contact [Your Contact Information]
