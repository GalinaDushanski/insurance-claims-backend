# Read this first!
This repository is a template repository for our technical interview, so create your own project using this guide:

[GitHub - Creating a repository from a template](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-repository-from-a-template).

An alternative is to download the code and create a new repository using a different VCS provider (gitlab / Azure repos). **Do not fork this repository.**

## Using AI assistance
We expect that many of you will use AI tools (LLMs, coding assistants, agents) while solving this task, and that is perfectly fine. Please keep the following in mind:

-  👀📖 **Your code will be read by humans.** Reviewers will read your solution before the interview, so aim for code that is clear, focused and easy to follow. More code is not better code.  This also applies to any documentation you write: use clear language intended for human readers. AI slop is frowned upon.
- **Feel free to share your AI artifacts.** If you used AI assistance, we encourage you to include the artifacts you created along the way, for example custom instructions, skills, a plan written to markdown, or session output. This is optional, but it helps us understand how you work.

## Submitting your solution
When you have completed the tasks, please share the repository link with us. We will review your submission before the interview.

When you are happy with your solution, it would be really helpful if you created a pull request where you explain your chain of thought. AI-assisted coding tends to produce a lot of code, so use it to guide us 🧭:
- Which parts of the code should we pay the most attention to?
- Which trade-offs did you make, and which assumptions did you rely on?
- What would you like to show and discuss during the interview? (And rest assured, we will come up with our own topics too 🙂) 

Good luck! 😊

# Prerequisites

- Your favourite IDE to code in C# 😊
- _Optional_ - an Azure Subscription. You can demo this API by hosting it in Azure. If that is not an option for you, you can run the demo having a locally running instance. If you select a different cloud provider, that is fine for us.
	- _This requires a rewrite of the setup (program.cs) of the application, as it currently depends on TestContainer packages to simplify the local development experience._
- `Docker Desktop` or a different Docker deamon running on your machine.

# Programming Task
Complete the backend for a multi-tier application for Insurance Claims Handling.
The use case is to maintain a list of insurance claims. The user should be able to create, delete and read claims.
## Task 1
The codebase is messy:
* The controller has too much responsibility. 
* Introduce proper layering within the codebase. 
* Documentation is missing.
* ++

Adhere to the SOLID principles.

### Task 2
As you can see, the API supports some basic REST operations. But validation is missing. The basic rules are:

* Claim
  * DamageCost cannot exceed 100.000
  * Created date must be within the period of the related Cover
* Cover
  * StartDate cannot be in the past
  * total insurance period cannot exceed 1 year

## Task 3
Auditing. The basics are there, but the execution of the DB command (INSERT & DELETE) blocks the processing of the HTTP request. How can this be improved? Look into some asynchronous patterns. It is ok to introduce an Azure managed service to help you with this (ServiceBus/EventGrid/Whatever), but that is not required. Whatever you can manage to get working which is in-memory is also ok.

## Task 4
One basic test is included, please add other (mandatory) unit tests. Note: If you start on this task first, you will find it hard to write proper tests. Some refactoring of the Claims API will be needed. 

## Task 5
Cover premium computation formula evolved over time. Fellow developers were lazy and omitted all tests. Now there are a couple of bugs. Can you fix them? Can you make the computation more readable?

Desired logic: 
* Premium depends on the type of the covered object and the length of the insurance period. 
* Base day rate was set to be 1250.
* Yacht should be 10% more expensive, Passenger ship 20%, Tanker 50%, and other types 30%
* The length of the insurance period should influence the premium progressively:
  * First 30 days are computed based on the logic above
  * Following 150 days are discounted by 5% for Yacht and by 2% for other types
  * The remaining days are discounted by additional 3% for Yacht and by 1% for other types
