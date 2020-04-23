using System.Threading.Tasks;
using EmailQueuer.Example.RazorModels;
using Microsoft.AspNetCore.Mvc;

namespace EmailQueuer.Example.Controllers
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
            var email = "om.mneimneh@gmail.com";
            await emailQueuer.EnqueueAsync(email, "Example", "Welcome", new Person
            {
                FirstName = "FName",
                LastName = "LName"
            });
            return Ok($"An email is on the way to you: {email}");
        }
    }
}