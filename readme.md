# LipoDoc Data Collector

A robust .NET 8 web application designed to collect, store, and manage data from blood lipemic testing devices over TCP/IP.

## Overview

LipoDoc Data Collector is a centralized system that provides:

- Real-time data collection from blood lipemic testing devices
- TCP/IP server for direct device communication
- Secure data storage in a MySQL database
- User-friendly web interface for monitoring and managing devices
- Role-based access control for system security
- Comprehensive donation data analytics and reporting

This application serves as a hub for blood donation centers to monitor their testing devices, collect lipemic test results, and ensure data integrity across multiple locations.

## Features

### Device Communication
- TCP/IP server that listens for incoming connections from testing devices on port 5000
- Support for proprietary LipoDoc device protocol
- Automatic device registration and status tracking
- Buffered data retrieval for devices with intermittent connectivity
- Remote device configuration and serial number management
- Time synchronization between server and devices

### Data Management
- Comprehensive blood donation lipemic test result storage
- Real-time device status monitoring and tracking
- Donation data filtering, sorting, and reporting
- Database backup and restore capabilities
- Historical data retention and automated cleanup

### Web Interface
- Real-time device monitoring dashboard with auto-refresh
- Detailed donation data visualization with filtering and sorting
- Device management and configuration
- User-friendly responsive design using Bootstrap 5
- Administrative tools for system maintenance

### Security
- Role-based authentication system (Admin/User roles)
- Secure password storage with BCrypt hashing
- Protected routes and views based on user permissions
- Audit logging for sensitive operations

## System Architecture

The application consists of:

1. **Web Interface** - ASP.NET MVC with Bootstrap 5
2. **TCP Server** - Background service for device communication
3. **Database** - MySQL with Entity Framework Core
4. **Background Services** - For monitoring, cleanup, and maintenance tasks

## Database Schema

### DonationsData
Stores the lipemic test results from blood donation devices:
- ID (auto-generated primary key)
- DeviceId (serial number of the device)
- Timestamp (when data was received)
- MessageType (the type of message: "#S", "#D", etc.)
- RawPayload (the raw message content)
- IP/Port information
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

### SystemNotifications
Stores system notifications for important events:
- ID (auto-generated primary key)
- Type (type of notification)
- Message (notification content)
- Timestamp (when the notification was created)
- Read (whether the notification has been read)
- RelatedEntityId (optional reference to related entity)

## Device Communication Protocol

The LipoDoc Data Collector communicates with devices using a proprietary protocol. Key message types include:

### Status Messages (`#S`)
```
#SªSNªStatusª"timestamp"ª"AvailableData"ª"CS"ý
```

### Data Messages (`#D`)
```
#DªSNªtimestampªBªRefCodeªDonationIdªOperatorIdªLotNumberªMªLipemicValueªLipemicGroupªLipemicStatusªENDEªCSý
```

### Serial Number Update Messages (`#i`)
```
#iªoldSNªnewSNª"CS"ýLF
```

### Request Buffered Data Messages (`#u`)
```
#uªSNªLF
```

The application handles these messages and more, providing a robust communication layer between devices and the server.

## Getting Started

### Prerequisites

- .NET 8.0 SDK
- MySQL Server 8.0 or higher
- Modern web browser
- Network access to connect with devices

### Installation

1. Clone the repository
2. Update the database connection string in `appsettings.json`
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

### Default Credentials

The system includes two predefined user accounts:

1. **Administrator**
   - Username: `admin`
   - Password: `admin123`
   - Full access to all features

2. **Regular User**
   - Username: `user`
   - Password: `user123`
   - Limited access (can view data but cannot modify system settings)

## Configuration

The main configuration is in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "server=localhost;port=3306;database=devicedata;user=root;password=root"
  },
  "TCPServer": {
    "IPAddress": "192.168.1.130",
    "Port": 5000
  },
  "DeviceStatusMonitor": {
    "CheckIntervalSeconds": 5,
    "InactiveThresholdSeconds": 10
  },
  "DeviceStatusCleanup": {
    "IntervalHours": 24,
    "RetentionDays": 30
  },
  "DatabaseBackup": {
    "Scheduled": {
      "Enabled": false,
      "Time": "08:00",
      "RetentionCount": 7,
      "IntervalHours": 24
    }
  }
}
```

Key settings:
- **DefaultConnection**: Your MySQL connection string
- **TCPServer:IPAddress**: The IP address to listen on for device connections
- **TCPServer:Port**: The port to listen on for device connections (default: 5000)
- **DeviceStatusMonitor**: Configuration for device status checking frequency
- **DeviceStatusCleanup**: Configuration for automatic data cleanup
- **DatabaseBackup**: Configuration for automated database backups

## Key Features In Detail

### Device Management

- Automatic device registration from incoming connections
- Real-time device status monitoring
- Remote serial number updates
- Device activity tracking and alerts

### Donation Data Collection

- Automatic parsing of device messages into structured data
- Support for multiple message formats
- Barcode scanning integration
- Lipemic value tracking and classification

### Backup and Recovery

- Manual and scheduled database backups
- Backup compression and management
- Database restoration capabilities
- Configurable retention policies

### Administrative Tools

- User management
- Network configuration
- Database management
- System monitoring

## Extending the Application

### Adding New Device Types

To add support for new device types:

1. Update the `DeviceMessageParser` class to handle new message formats
2. Add any new fields to the data models if needed
3. Create migrations to update the database schema

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
