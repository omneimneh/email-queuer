using System.Threading.Tasks;
using CodeSwitch.Utils.EmailQueuer.Example.RazorModels;
using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.AspNetCore.Mvc;

namespace CodeSwitch.Utils.EmailQueuer.Example.Controllers
{
    public class HomeController : Controller
    {
        private readonly EmailQueuer<AppDbContext> emailQueuer;

        public HomeController(EmailQueuer<AppDbContext> emailQueuer)
        {
            this.emailQueuer = emailQueuer;
        }

        public async Task<IActionResult> Index()
        {
            var email = "salah.khat96@gmail.com";
            await emailQueuer.EnqueueAsync(email, "Example", "Welcome", new Person
            {
                FirstName = "FName",
                LastName = "LName"
            });
            return Ok($"An email is on the way to you: {email}");
        }

        // /Home/Bulk : queues a burst of bulk mail for two tenants, then one normal email.
        // The normal email (priority 0) is sent first although it is queued last, and the two
        // tenants take turns (A, B, A, B, A, B, A, A...) instead of B waiting behind all of A.
        public async Task<IActionResult> Bulk()
        {
            var email = "salah.khat96@gmail.com";
            for (var i = 1; i <= 5; i++)
            {
                await emailQueuer.EnqueueAsync(email, $"Tenant A digest {i}", "Welcome", new Person
                {
                    FirstName = "Tenant",
                    LastName = "A"
                }, new EmailEnqueueOptions { Priority = -10, PartitionKey = "tenant:A" });
            }
            for (var i = 1; i <= 3; i++)
            {
                await emailQueuer.EnqueueAsync(email, $"Tenant B digest {i}", "Welcome", new Person
                {
                    FirstName = "Tenant",
                    LastName = "B"
                }, new EmailEnqueueOptions { Priority = -10, PartitionKey = "tenant:B" });
            }
            await emailQueuer.EnqueueAsync(email, "Example", "Welcome", new Person
            {
                FirstName = "FName",
                LastName = "LName"
            });
            return Ok($"8 bulk emails and 1 normal email are on the way to you: {email}");
        }
    }
}
