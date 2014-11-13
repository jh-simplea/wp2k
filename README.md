# WordPress 2 Kentico Blog Migration Tool

This tool is built to aid in migrating a WordPress blog to the Kentico CMS platform. It works by parsing the XML WordPress export file and inserting the data into an existing Kentico database. The newly-created blog entries can then be accessed and edited just like any other document created in the Kentico system.

The goal of the WP2K application is to not only import a WordPress blog to Kentico, but to help maintain the original path structure and ID links from the original blog.

## System Requirements

- .Net 4.0+, or .Net 3.0+ with Entity Framework, LINQ
- SQL Server 2008+
- Microsoft Visual Studio 2010+

**Recommended:** Kentico 7+, WordPress 3+

## How to Use

1. Export your WordPress blog to XML using the WordPress exporter tool.
2. Install Kentico to your target location.
3. Download the WP2K tool and edit the AppSettings and connection sections of App.config for your setup.
 * You can use an existing Blog section, or allow the tool to create a new one<sup><a href="#note1">1</a></sup>
4. Compile the application and run it from Visual Studio<sup><a href="#note2">2</a></sup>.

<sup id="note1">1</sup> While the tool does create the Blog section, there is currently a known issue where the document isn't completely created, and the CMS doesn't entirely recognize it. 
<sup id="note2">2</sup> It is possible to install the application, but it is not currently built to do so. The application will run immediately upon successful installation.

### Settings

Because of the variable nature of Kentico setups, the application comes with a number of settings. They can all be found at the beginning of the App.config file.

* Database Connection and Entity String - Set these as necessary. By default, they are set up to access a SQL Server database.
* file - The full path to the WordPress blog export file.
* kenticoBlogId - The ID of the blog in Kentico to use. If no item of Blog type exists at this ID, then the system will create a new one.
* kenticoBlogLevel - The NodeLevel of the Blog Node. 1 is directly under the site root.
* kenticoBlogPath - The parent Nodes of the Blog Node, if kenticoBlogLevel is greater than 1. Leave blank if kenticoBlogLevel is 1.
* nodeOwnerId - The default ID of all Nodes created by the system. Defaults to the site administrator (53), but can be set to any valid User ID.
* tagGroupName - The name for the Tag Group that all transferred tags will be filed under. Required to connect tags to posts.
* siteId - The ID of the Kentico Site the blog belongs to.
* xmlTrumpsDb - For posts, this provides the option to allow or deny the XML file's ability to overwrite existing blog entries with the same (BlogPost) ID.
 
### License

Released under the [DBAD Public License](http://www.dbad-license.org/). Fork it to your heart's content. Update it, fix it, re-release it with your changes. Just don't be a dick. If you make money off it, it'd be nice (though not required) to get me coffee or lunch money in return. Like most open source stuff, this project is provided as is and with no warranty.
