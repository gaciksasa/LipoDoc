# IQ Link - Device Data Collection Platform

IQ Link is a centralized device management platform designed for collecting, monitoring, and analyzing data from devices connected via TCP/IP. It's particularly focused on managing devices that perform lipemic tests on blood donations.

## Features

### Dashboard
- Real-time system status monitoring
- Quick access to key metrics (donations, devices, users)
- System uptime tracking

### Device Management
- Automatic registration of devices on first connection
- Device activity monitoring
- Device configuration and settings management
- Serial number updates
- Request and apply device setup changes

### Donation Data Management
- View and filter donation records
- Real-time data updates
- Advanced data export with customizable formats
- Track lipemic test results (values, groups, status)

### User Management
- Role-based authentication (Admin/User roles)
- User profile management
- Secure password storage

### System Settings
- Network configuration
- Database connection settings
- Backup and restore functionality

## Technical Details

### Network Communication
- The application acts as a TCP server listening on port 5000
- Devices connect to this port and exchange data using a proprietary protocol
- No need to manually configure IP addresses of remote devices - they automatically connect to the server

### Data Storage
- Uses MySQL database for data persistence
- Supports database backup and restore functionality
- Automatic database migration on startup

## Getting Started

### Prerequisites
- .NET 6.0 SDK or later
- MySQL Server 5.7 or later

### Installation

1. **Clone the repository:**
   ```
   git clone https://github.com/yourusername/DeviceDataCollector.git
   cd DeviceDataCollector
   ```

2. **Update database connection string:**
   - Open `appsettings.json`
   - Modify the `ConnectionStrings:DefaultConnection` to match your MySQL server settings:
     ```json
     "ConnectionStrings": {
       "DefaultConnection": "server=localhost;port=3306;database=devicedata;user=root;password=your_password"
     }
     ```

3. **Build and run the application:**
   ```
   dotnet build
   dotnet run
   ```
   
4. **Access the application:**
   - Open a web browser and navigate to `http://localhost:5000`
   - Default login credentials:
     - Admin: Username: `admin`, Password: `admin123`
     - User: Username: `user`, Password: `user123`

### Configuring TCP Server

By default, the TCP server binds to the IP address specified in `appsettings.json` under `TCPServer:IPAddress` and listens on the port specified in `TCPServer:Port`. The default port is 5000.

```json
"TCPServer": {
  "IPAddress": "192.168.1.130",
  "Port": 5000
}
```

Make sure to set the IP address to one of your machine's network interfaces that the devices can reach.

## Working with Devices

### Device Protocol

The application communicates with devices using a specialized protocol:

1. Devices send status updates (`#S` messages)
2. Devices send data readings (`#D` messages)
3. Server can request buffered data (`#u` messages)
4. Server can update device configuration (`#W` messages)
5. Server can update device serial numbers (`#i` messages)

### Device Setup Mode

To configure a device:
1. Put the device into setup mode (usually through device's interface)
2. The device status will change to "Setup Mode" (Status = 3) in the system
3. Click "Request Setup" button to retrieve current configuration
4. Make desired changes and click "Save Setup" to apply them

## User Guide

### Viewing Donation Data

1. Click on "Donations" in the sidebar
2. Use filters and sorting options to find specific records
3. Click on "Details" for more information about a specific donation

### Exporting Data

1. Navigate to Donations → Export
2. Select desired columns and configure export format
3. Set date range and device filters
4. Click "Export to CSV"
5. Save your export configuration for future use

### Device Management

1. Click on "Devices" in the sidebar
2. View all registered devices and their current status
3. Click on a device to view details and recent readings
4. Use the Edit option to update device information

### System Administration (Admin Only)

1. **User Management:**
   - Create, edit, or delete user accounts
   - Assign Admin or User roles

2. **Network Settings:**
   - Configure TCP server settings
   - View available network interfaces

3. **Database Settings:**
   - Configure database connection
   - Test database connectivity

4. **Backup and Restore:**
   - Create manual or scheduled backups
   - Restore from previous backups

## Troubleshooting

### Device Connection Issues

- Ensure the TCP server IP address is correctly set to your machine's IP
- Verify port 5000 is not blocked by a firewall
- Check that devices are configured to connect to the correct server IP and port

### Database Issues

- Verify MySQL server is running
- Check database connection string in appsettings.json
- Ensure the specified database exists or the user has permission to create it

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For support and further information, please contact the development team.