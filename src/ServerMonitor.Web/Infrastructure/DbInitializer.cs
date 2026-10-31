using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Web.Infrastructure;

public static class DbInitializer
{
    /// <summary>Applies migrations, creates roles and the first administrator.</summary>
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning("Database not ready ({Message}); retrying in {Delay}s", ex.Message, attempt * 2);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.Users.AnyAsync()) return;

        var email = config["Seed:AdminEmail"] ?? "admin@local";
        var password = config["Seed:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = GeneratePassword();

        var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
        var result = await users.CreateAsync(admin, password!);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not create the initial admin: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(admin, Roles.Admin);

        if (generated)
        {
            // Never log the password itself: write it to a file next to the key ring that only the service account can read.
            var dir = config["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "keys");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "initial-admin-password.txt");
            await File.WriteAllTextAsync(file, $"{email}\n{password}\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogWarning("Initial administrator '{Email}' created. Its generated password was written to {File}. Change it after first login and delete the file.", email, file);
        }
        else
        {
            logger.LogInformation("Initial administrator '{Email}' created from configuration.", email);
        }
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%^*-_";
        var core = RandomNumberGenerator.GetString(chars, 20);
        return core + "Aa1!"; // guarantee every character class required by the password policy
    }
}
